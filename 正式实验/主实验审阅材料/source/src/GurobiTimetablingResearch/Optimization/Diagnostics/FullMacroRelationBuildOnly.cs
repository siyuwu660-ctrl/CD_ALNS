using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Gurobi;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

public sealed record FullMacroRelationSectionCount(
    string SectionName,
    int TraversalCount,
    int TotalRelationCount,
    int SameDirectionCount,
    int OppositeDirectionSingleTrackCount,
    int OppositeDirectionOtherCount);

public sealed record FullMacroRelationBuildOnlyReport(
    string InstanceName,
    int TrainCount,
    int StationCount,
    int MicroRequirementCount,
    int TotalRelationCount,
    int SameDirectionRelationCount,
    int OppositeDirectionSingleTrackRelationCount,
    int OppositeDirectionOtherRelationCount,
    int VariableCount,
    int BinaryVariableCount,
    int ConstraintCount,
    int NonzeroCount,
    int PresolvedVariableCount,
    int PresolvedBinaryVariableCount,
    int PresolvedConstraintCount,
    int PresolvedNonzeroCount,
    double RelationEnumerationSeconds,
    double ModelBuildSeconds,
    double TotalRuntimeSeconds,
    long WorkingSetBeforeBytes,
    long WorkingSetAfterBytes,
    long PrivateMemoryBeforeBytes,
    long PrivateMemoryAfterBytes,
    int ModelStatusCode,
    string ModelStatus,
    int SolutionCount,
    int OptimizeCallCount,
    bool PresolveInvoked,
    bool LpExported,
    IReadOnlyList<FullMacroRelationSectionCount> SectionCounts);

public static class FullMacroRelationBuildOnlyRunner
{
    public static FullMacroRelationBuildOnlyReport Run(
        TimetablingInstance instance,
        string outputDirectory,
        double runningTimeSupplementRatio,
        int endpointOccupationSeconds)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        Process process = Process.GetCurrentProcess();
        process.Refresh();
        long workingBefore = process.WorkingSet64;
        long privateBefore = process.PrivateMemorySize64;
        Stopwatch totalClock = Stopwatch.StartNew();
        Stopwatch enumerationClock = Stopwatch.StartNew();
        IReadOnlyList<SectionConflictCandidate> allRelations =
            new InitialConflictGraphBuilder(instance, int.MaxValue).Build();
        IReadOnlyList<FullMacroRelationSectionCount> sectionCounts = BuildSectionCounts(
            instance,
            allRelations);
        enumerationClock.Stop();

        Stopwatch buildClock = Stopwatch.StartNew();
        using BasicMilpModel model = new BasicMilpModelBuilder(
            instance,
            neighborRadius: int.MaxValue,
            runningTimeSupplementRatio: runningTimeSupplementRatio,
            endpointOccupationSeconds: endpointOccupationSeconds).Build();
        buildClock.Stop();
        using GRBModel presolvedModel = model.Model.Presolve();
        int status = model.Model.Get(GRB.IntAttr.Status);
        int solutionCount = model.Model.Get(GRB.IntAttr.SolCount);
        if (status != GRB.Status.LOADED || solutionCount != 0)
        {
            throw new InvalidOperationException(
                $"Build-only model unexpectedly left LOADED state: status={status}, solutions={solutionCount}.");
        }
        totalClock.Stop();
        process.Refresh();
        FullMacroRelationBuildOnlyReport report = new(
            instance.Meta.InstanceName,
            instance.Trains.Count,
            instance.Stations.Count,
            instance.TrainStops.Count,
            allRelations.Count,
            sectionCounts.Sum(item => item.SameDirectionCount),
            sectionCounts.Sum(item => item.OppositeDirectionSingleTrackCount),
            sectionCounts.Sum(item => item.OppositeDirectionOtherCount),
            model.Model.Get(GRB.IntAttr.NumVars),
            model.Model.Get(GRB.IntAttr.NumBinVars),
            model.Model.Get(GRB.IntAttr.NumConstrs),
            model.Model.Get(GRB.IntAttr.NumNZs),
            presolvedModel.Get(GRB.IntAttr.NumVars),
            presolvedModel.Get(GRB.IntAttr.NumBinVars),
            presolvedModel.Get(GRB.IntAttr.NumConstrs),
            presolvedModel.Get(GRB.IntAttr.NumNZs),
            enumerationClock.Elapsed.TotalSeconds,
            buildClock.Elapsed.TotalSeconds,
            totalClock.Elapsed.TotalSeconds,
            workingBefore,
            process.WorkingSet64,
            privateBefore,
            process.PrivateMemorySize64,
            status,
            "LOADED",
            solutionCount,
            OptimizeCallCount: 0,
            PresolveInvoked: true,
            LpExported: false,
            sectionCounts);
        Write(report, outputDirectory);
        return report;
    }

    private static IReadOnlyList<FullMacroRelationSectionCount> BuildSectionCounts(
        TimetablingInstance instance,
        IReadOnlyList<SectionConflictCandidate> relations)
    {
        Dictionary<(string SectionName, int TrainIndex), TravelDirection> directionByTraversal = new();
        foreach (SectionRecord section in instance.Sections)
        {
            foreach (TrainRecord train in instance.Trains)
            {
                for (int index = 0; index < train.OrderedStops.Count - 1; index++)
                {
                    TrainStopRecord start = train.OrderedStops[index];
                    TrainStopRecord end = train.OrderedStops[index + 1];
                    if (!string.Equals(end.IncomingSectionName, section.SectionName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    directionByTraversal[(section.SectionName, train.TrainIndex)] =
                        ResolveDirection(section, start.StationName, end.StationName);
                    break;
                }
            }
        }

        List<FullMacroRelationSectionCount> rows = [];
        foreach (SectionRecord section in instance.Sections)
        {
            IReadOnlyList<SectionConflictCandidate> sectionRelations = relations
                .Where(item => string.Equals(item.SectionName, section.SectionName,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            int same = 0;
            int oppositeSingle = 0;
            int oppositeOther = 0;
            foreach (SectionConflictCandidate relation in sectionRelations)
            {
                TravelDirection first = directionByTraversal[(section.SectionName, relation.FirstTrainIndex)];
                TravelDirection second = directionByTraversal[(section.SectionName, relation.SecondTrainIndex)];
                if (first == second)
                {
                    same++;
                }
                else if (section.SectionType.Contains("单线", StringComparison.OrdinalIgnoreCase))
                {
                    oppositeSingle++;
                }
                else
                {
                    oppositeOther++;
                }
            }
            int traversalCount = directionByTraversal.Keys.Count(key =>
                string.Equals(key.SectionName, section.SectionName, StringComparison.OrdinalIgnoreCase));
            rows.Add(new FullMacroRelationSectionCount(
                section.SectionName,
                traversalCount,
                sectionRelations.Count,
                same,
                oppositeSingle,
                oppositeOther));
        }
        return rows;
    }

    private static TravelDirection ResolveDirection(
        SectionRecord section,
        string startStation,
        string endStation)
    {
        if (string.Equals(section.FromStation, startStation, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(section.ToStation, endStation, StringComparison.OrdinalIgnoreCase))
        {
            return section.DirectionTag;
        }
        if (string.Equals(section.ToStation, startStation, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(section.FromStation, endStation, StringComparison.OrdinalIgnoreCase))
        {
            return section.DirectionTag switch
            {
                TravelDirection.Up => TravelDirection.Down,
                TravelDirection.Down => TravelDirection.Up,
                _ => TravelDirection.Unknown
            };
        }
        throw new InvalidDataException(
            $"Cannot determine traversal direction for section '{section.SectionName}' and stations " +
            $"'{startStation}'/'{endStation}'.");
    }

    private static void Write(FullMacroRelationBuildOnlyReport report, string outputDirectory)
    {
        JsonSerializerOptions options = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        File.WriteAllText(
            Path.Combine(outputDirectory, "full_macro_relation_build_only.json"),
            JsonSerializer.Serialize(report, options),
            new UTF8Encoding(false));
        StringBuilder csv = new();
        csv.AppendLine("section_name,traversal_count,total_relation_count,same_direction_count,opposite_direction_single_track_count,opposite_direction_other_count");
        foreach (FullMacroRelationSectionCount row in report.SectionCounts)
        {
            csv.Append(Escape(row.SectionName)).Append(',')
                .Append(row.TraversalCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(row.TotalRelationCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(row.SameDirectionCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(row.OppositeDirectionSingleTrackCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(row.OppositeDirectionOtherCount.ToString(CultureInfo.InvariantCulture)).AppendLine();
        }
        File.WriteAllText(
            Path.Combine(outputDirectory, "full_macro_relation_counts.csv"),
            csv.ToString(),
            new UTF8Encoding(false));
    }

    private static string Escape(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
}
