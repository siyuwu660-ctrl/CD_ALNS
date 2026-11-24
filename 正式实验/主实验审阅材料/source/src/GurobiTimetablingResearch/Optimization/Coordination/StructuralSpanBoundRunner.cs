using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using GurobiTimetablingResearch.IO;

namespace GurobiTimetablingResearch.Optimization;

internal static class StructuralSpanBoundRunner
{
    internal static int Run(string[] args)
    {
        try
        {
            var clock=Stopwatch.StartNew();var values=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(string arg in args) {
                if(arg=="--structural-span-bound")continue;
                int at=arg.IndexOf('=');
                if(at<1 || !values.TryAdd(arg[..at],arg[(at+1)..]))throw new ArgumentException("Malformed or duplicate certificate argument.");
            }
            string[] allowed=["--dataset","--certificate-output","--bound-timing-tolerance"];
            if(values.Keys.Except(allowed).Any())throw new ArgumentException("Certificate accepts no incumbent, local fixing, solver or target-gap options.");
            string Need(string key)=>values.GetValueOrDefault(key)??throw new ArgumentException("Missing "+key);
            string dataset=Path.GetFullPath(Need("--dataset")),output=Path.GetFullPath(Need("--certificate-output"));
            if(File.Exists(output) || Directory.Exists(output))throw new IOException("Refusing to overwrite certificate evidence.");
            double tolerance=double.Parse(values.GetValueOrDefault("--bound-timing-tolerance","0"),CultureInfo.InvariantCulture);
            var hashes=HashDataset(dataset);
            var instance=new NormalizedDatasetLoader().Load(dataset);
            var proof=StructuralSpanBound.Build(instance,tolerance);
            var capacityProof=StructuralCapacitySpanBound.Build(instance,proof);
            double lowerBoundSeconds=Math.Max(proof.LowerBoundSeconds,capacityProof.LowerBoundSeconds??double.NegativeInfinity);
            if(!hashes.SequenceEqual(HashDataset(dataset)))throw new IOException("Dataset changed during certificate construction.");
            string binary=Assembly.GetExecutingAssembly().Location;
            var provenance=VerifyCompiledSources(binary);
            var evidence=new {
                kind="AnalyticalGlobalSpanLowerBound",executedAt=DateTimeOffset.Now,command=Environment.GetCommandLineArgs(),
                dataset, dataFiles=hashes, sourceBinaryBinding="PortablePdbVerified",
                sourceFiles=provenance.SourceFiles,pdbPath=provenance.PdbPath,pdbSha256=provenance.PdbSha256,
                binaryPath=binary,binarySha256=HashFile(binary),
                machine=Environment.MachineName,runtime=Environment.Version.ToString(),optimizerInvoked=false,
                seconds=clock.Elapsed.TotalSeconds,
                proofFormula="F_upper=min(min_origin(latest_departure+eps),min_r(b_r+eps-p_r[k])); L_lower=max(max_destination(earliest_arrival-eps),max_r(a_r-eps+q_r[k])); LB=max(max_train_min_duration,L_lower-F_upper)-1e-6",
                capacityFormula="LB_section=min(prefix)+(n-1)*max(0,min_all_orders_modes_domains(headway)-eps)+min(suffix)-1e-6; combined LB=max(quota_runtime_LB,all_section_LB)",
                omittedConstraints="Micro resource assignments, overtaking, maintenance, section stop quotas and max travel times; these only enlarge the domain. Full-domain section headways retained only in the capacity proof. No preferred timetable or incumbent is used.",
                lowerBoundSeconds,proof,capacityProof
            };
            byte[] json=JsonSerializer.SerializeToUtf8Bytes(evidence,new JsonSerializerOptions { WriteIndented=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase });
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using(var file=new FileStream(output,FileMode.CreateNew,FileAccess.Write,FileShare.Read))file.Write(json);
            Console.WriteLine(JsonSerializer.Serialize(new { certificate=output,instance=proof.InstanceName,LowerBoundSeconds=lowerBoundSeconds,
                proof.FirstDepartureUpperSeconds,proof.LastArrivalLowerSeconds,tolerance,seconds=clock.Elapsed.TotalSeconds,optimizerInvoked=false }));
            return 0;
        }
        catch(Exception ex) { Console.Error.WriteLine(ex);return 2; }
    }
    internal sealed record CompiledSourceEvidence(string PdbPath,string PdbSha256,IReadOnlyDictionary<string,string> SourceFiles);
    internal static CompiledSourceEvidence VerifyCompiledSources(string binary)
    {
        string pdbPath=Path.ChangeExtension(binary,".pdb");
        using var peStream=File.OpenRead(binary);
        using var pe=new PEReader(peStream);
        using var pdbStream=File.OpenRead(pdbPath);
        using var provider=MetadataReaderProvider.FromPortablePdbStream(pdbStream);
        var reader=provider.GetMetadataReader();
        var contentId=new BlobContentId(reader.DebugMetadataHeader!.Id);
        var codeViewEntries=pe.ReadDebugDirectory().Where(e=>e.Type==DebugDirectoryEntryType.CodeView).ToArray();
        if(!codeViewEntries.Any(e=>pe.ReadCodeViewDebugDirectoryData(e).Guid==contentId.Guid && e.Stamp==contentId.Stamp))
            throw new InvalidDataException("Portable PDB does not match the executable.");
        var sources=new SortedDictionary<string,string>(StringComparer.Ordinal);
        foreach(var handle in reader.Documents)
        {
            var document=reader.GetDocument(handle);
            string path=reader.GetString(document.Name);
            byte[] bytes=ReadCompiledSource(reader,handle,path);
            Guid algorithm=reader.GetGuid(document.HashAlgorithm);
            byte[] actual=algorithm==new Guid("8829d00f-11b8-4213-878b-770e8597ac16")?SHA256.HashData(bytes)
                :algorithm==new Guid("ff1816ec-aa5e-4d10-87f7-6f4963833460")?SHA1.HashData(bytes)
                :throw new InvalidDataException("Unsupported compiler source checksum.");
            if(!actual.SequenceEqual(reader.GetBlobBytes(document.Hash)))
                throw new InvalidDataException("Source differs from the compiled executable: "+path);
            sources.Add(path,Convert.ToHexString(SHA256.HashData(bytes)));
        }
        if(sources.Count==0)throw new InvalidDataException("No compiled source checksums were found.");
        return new(pdbPath,HashFile(pdbPath),sources);
    }
    private static byte[] ReadCompiledSource(MetadataReader reader,DocumentHandle handle,string path)
    {
        if(File.Exists(path))return File.ReadAllBytes(path);
        // Source generators can emit virtual documents. The compiler embeds their exact
        // bytes in the matching PDB; still verify the document checksum below.
        foreach(var item in reader.GetCustomDebugInformation(handle))
        {
            var info=reader.GetCustomDebugInformation(item);
            if(reader.GetGuid(info.Kind)!=new Guid("0e8a571b-6926-466e-b4ad-8ab04611f5fe"))continue;
            byte[] blob=reader.GetBlobBytes(info.Value);
            if(blob.Length<4)throw new InvalidDataException("Invalid embedded compiler source.");
            int size=System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(blob);
            if(size==0)return blob[4..];
            if(size<0)throw new InvalidDataException("Invalid embedded source size.");
            using var compressed=new MemoryStream(blob,4,blob.Length-4);
            using var deflate=new DeflateStream(compressed,CompressionMode.Decompress);
            using var output=new MemoryStream();
            deflate.CopyTo(output);
            if(output.Length!=size)throw new InvalidDataException("Embedded source size mismatch.");
            return output.ToArray();
        }
        throw new FileNotFoundException("Compiled source is neither on disk nor embedded in the PDB.",path);
    }
    internal static SortedDictionary<string,string> HashDataset(string root)
        =>new(Directory.GetFiles(root,"*",SearchOption.AllDirectories).ToDictionary(p=>Path.GetRelativePath(root,p),HashFile),StringComparer.Ordinal);
    private static string HashFile(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
