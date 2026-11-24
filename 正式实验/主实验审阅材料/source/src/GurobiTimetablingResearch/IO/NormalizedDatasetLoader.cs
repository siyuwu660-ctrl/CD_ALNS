// 标准化数据装载器逐表读取列车、区间、运行时分、天窗、径路及间隔 CSV，并用复合键字典连接跨表记录。
// 装载过程完成时间单位转换、方向展开、字段校验和走廊顺序推导，最终构造供所有求解器共享的 TimetablingInstance。
using GurobiTimetablingResearch.Domain;
using GurobiTimetablingResearch.Optimization;

namespace GurobiTimetablingResearch.IO;

/// <summary>
/// 从统一后的 Data 目录加载标准化实例。
/// 当前默认 JZ/JH 原始数据已经在仓库内被整理为统一字段口径，
/// 因此这里的重点是把 CSV 映射为后续建模直接使用的标准对象。
/// </summary>
public sealed class NormalizedDatasetLoader
{
    private readonly SimpleCsvReader _csvReader = new();

    /// <summary>Load and validate the normalized timetable input tables.</summary>
    /// <param name="datasetDirectory">Directory supplying the original CSV data.</param>
    /// <returns>The normalized instance with times expressed in seconds.</returns>
    /// <exception cref="DirectoryNotFoundException">The input directory does not exist.</exception>
    /// <exception cref="InvalidDataException">A required field or an input rule is invalid.</exception>
    public TimetablingInstance Load(string datasetDirectory)
    {
        if (!Directory.Exists(datasetDirectory))
        {
            throw new DirectoryNotFoundException($"Dataset directory was not found: {datasetDirectory}");
        }

        string instanceName = new DirectoryInfo(datasetDirectory).Name;

        CorridorDefinition corridor = LoadCorridor(datasetDirectory);
        IReadOnlyList<string> stations = LoadStations(datasetDirectory);
        IReadOnlyList<SectionRecord> sections = LoadSections(datasetDirectory);
        IReadOnlyList<RunTimeRule> runTimeRules = LoadRunTimeRules(datasetDirectory);
        IReadOnlyList<MaintenanceWindowRule> maintenanceWindows = LoadMaintenanceWindows(datasetDirectory);
        IReadOnlyList<StationStopQuotaRule> stationStopQuotaRules = LoadStationStopQuotaRules(datasetDirectory, stations);
        IReadOnlyList<TrainSectionStopQuotaRule> trainSectionStopQuotaRules = LoadTrainSectionStopQuotaRules(datasetDirectory);
        IReadOnlyList<TrainSectionTravelTimeLimitRule> trainSectionTravelTimeLimitRules = LoadTrainSectionTravelTimeLimitRules(datasetDirectory);
        IReadOnlyList<RouteRecord> routes = LoadRoutes(datasetDirectory);
        IReadOnlyList<RouteHeadwayRule> routeHeadwayRules = LoadRouteHeadwayRules(datasetDirectory);
        IReadOnlyList<MovementHeadwayRule> movementHeadwayRules = LoadMovementHeadwayRules(datasetDirectory, routes);
        (IReadOnlyList<TrainRecord> trains, IReadOnlyList<TrainStopRecord> stops) = LoadTrains(datasetDirectory);
        IReadOnlyList<NormalDwellTimeRule> normalDwellTimeRules = LoadNormalDwellTimeRules(datasetDirectory);
        IReadOnlyList<DatasetDiagnostic> datasetDiagnostics = ValidateNormalDwellTimeRules(normalDwellTimeRules, stops);
        IReadOnlyList<TrainTrackChoiceRecord> trainTrackChoiceRecords = LoadTrainTrackChoiceRecords(datasetDirectory);
        IReadOnlyList<BenchmarkTrackRestriction> benchmarkTrackRestrictions = LoadBenchmarkTrackRestrictions(datasetDirectory);
        IReadOnlyList<StationOvertakingProfile> stationOvertakingProfiles =
            LoadStationOvertakingProfiles(datasetDirectory, stations, routes, stops);

        return new TimetablingInstance(
            new InstanceMeta(instanceName, datasetDirectory, "second"),
            corridor,
            stations,
            sections,
            trains,
            stops,
            runTimeRules,
            maintenanceWindows,
            stationStopQuotaRules,
            trainSectionStopQuotaRules,
            trainSectionTravelTimeLimitRules,
            routes,
            movementHeadwayRules,
            routeHeadwayRules,
            trainTrackChoiceRecords,
            stationOvertakingProfiles,
            benchmarkTrackRestrictions,
            normalDwellTimeRules,
            datasetDiagnostics);
    }

    private IReadOnlyList<NormalDwellTimeRule> LoadNormalDwellTimeRules(string datasetDirectory)
    {
        string path = Path.Combine(datasetDirectory, "\u65e0\u5f85\u907f\u505c\u7ad9\u65f6\u95f4.csv");
        if (!File.Exists(path))
        {
            return Array.Empty<NormalDwellTimeRule>();
        }

        List<NormalDwellTimeRule> rules = _csvReader.ReadRows(path)
            .Select(row => new NormalDwellTimeRule(
                ParseRequiredInt(row, "\u5217\u8f66\u5e8f\u53f7"),
                ParseRequiredInt(row, "\u7ad9\u987a"),
                GetRequired(row, "\u8f66\u7ad9\u540d\u79f0"),
                ParseRequiredInt(row, "\u6700\u5c0f\u505c\u7ad9\u65f6\u95f4"),
                ParseRequiredInt(row, "\u6700\u5927\u505c\u7ad9\u65f6\u95f4")))
            .ToList();
        NormalDwellRuleIndex.Create(rules);
        return rules;
    }

    private static IReadOnlyList<DatasetDiagnostic> ValidateNormalDwellTimeRules(
        IReadOnlyList<NormalDwellTimeRule> rules,
        IReadOnlyList<TrainStopRecord> stops)
    {
        Dictionary<(int TrainIndex, int StationOrder), TrainStopRecord> stopsByKey = stops.ToDictionary(
            stop => (stop.TrainIndex, stop.StationOrder));
        foreach (NormalDwellTimeRule rule in rules)
        {
            if (!stopsByKey.TryGetValue((rule.TrainIndex, rule.StationOrder), out TrainStopRecord? stop))
            {
                throw new InvalidDataException(
                    $"Normal dwell rule has no matching train stop: train {rule.TrainIndex}, order {rule.StationOrder}.");
            }
            _ = NormalDwellTimeResolver.Resolve(rules, stop);
        }

        HashSet<(int TrainIndex, int StationOrder)> ruleKeys = rules
            .Select(rule => (rule.TrainIndex, rule.StationOrder))
            .ToHashSet();
        return stops
            .Where(stop => stop.StopRole == TrainStopRole.Intermediate &&
                !ruleKeys.Contains((stop.TrainIndex, stop.StationOrder)))
            .Select(stop => new DatasetDiagnostic(
                "missing_normal_dwell_rule",
                "Normal dwell rule is missing; original TrainStopRecord dwell bounds are used.",
                stop.TrainIndex,
                stop.StationOrder,
                stop.StationName))
            .ToList();
    }

    private CorridorDefinition LoadCorridor(string datasetDirectory)
    {
        string path = Path.Combine(datasetDirectory, "!\u8ba1\u7b97\u76ee\u6807\u533a\u6bb5.csv");
        IReadOnlyList<IReadOnlyDictionary<string, string>> rows = _csvReader.ReadRows(path);

        List<(int Order, string SectionName)> ordered = rows
            .Select(row => (Order: ParseRequiredInt(row, "\u533a\u95f4\u987a\u5e8f"), SectionName: GetRequired(row, "\u533a\u95f4\u540d\u79f0")))
            .OrderBy(item => item.Order)
            .ToList();

        IReadOnlyList<string> sectionNames = ordered.Select(item => item.SectionName).ToList();
        IReadOnlyDictionary<string, int> sectionOrderMap = ordered.ToDictionary(item => item.SectionName, item => item.Order, StringComparer.OrdinalIgnoreCase);

        return new CorridorDefinition(sectionNames, sectionOrderMap);
    }

    private IReadOnlyList<string> LoadStations(string datasetDirectory)
    {
        string path = Path.Combine(datasetDirectory, "\u8f66\u7ad9.csv");
        return _csvReader.ReadRows(path)
            // JZ instance 使用“车站名称”，JH instance 的车站表只有“车站编号”。
            // 两套数据在其它表里都用“站xxxx”作为站点标识，因此这里把二者统一读取为 station name。
            .Select(row => GetRequiredAny(row, "\u8f66\u7ad9\u540d\u79f0", "\u8f66\u7ad9\u7f16\u53f7"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IReadOnlyList<SectionRecord> LoadSections(string datasetDirectory)
    {
        string path = Path.Combine(datasetDirectory, "\u533a\u95f4.csv");
        return _csvReader.ReadRows(path)
            .Select(row => new SectionRecord(
                GetRequired(row, "\u533a\u95f4\u540d\u79f0"),
                GetRequired(row, "\u533a\u95f4\u524d\u7ad9"),
                GetRequired(row, "\u533a\u95f4\u540e\u7ad9"),
                ParseDirection(GetRequired(row, "\u533a\u95f4\u884c\u522b")),
                GetRequired(row, "\u533a\u95f4\u6027\u8d28"),
                ParseNullableDouble(row, "\u533a\u95f4\u957f\u5ea6")))
            .ToList();
    }

    private IReadOnlyList<RunTimeRule> LoadRunTimeRules(string datasetDirectory)
    {
        string path = Path.Combine(datasetDirectory, "\u8fd0\u884c\u6807\u5c3a.csv");
        List<RunTimeRule> rules = new();

        foreach (IReadOnlyDictionary<string, string> row in _csvReader.ReadRows(path))
        {
            string scaleName = GetRequired(row, "\u6807\u5c3a\u540d\u79f0");
            string sectionName = GetRequired(row, "\u533a\u95f4\u540d\u79f0");

            rules.Add(new RunTimeRule(
                scaleName,
                sectionName,
                TravelDirection.Up,
                ParseRequiredInt(row, "\u8fd0\u884c\u65f6\u5206\uff08\u4e0a\u884c\uff09"),
                ParseRequiredInt(row, "\u8d77\u8f66\u9644\u52a0\uff08\u4e0a\u884c\uff09"),
                ParseRequiredInt(row, "\u505c\u8f66\u9644\u52a0\uff08\u4e0a\u884c\uff09")));

            rules.Add(new RunTimeRule(
                scaleName,
                sectionName,
                TravelDirection.Down,
                ParseRequiredInt(row, "\u8fd0\u884c\u65f6\u5206\uff08\u4e0b\u884c\uff09"),
                ParseRequiredInt(row, "\u8d77\u8f66\u9644\u52a0\uff08\u4e0b\u884c\uff09"),
                ParseRequiredInt(row, "\u505c\u8f66\u9644\u52a0\uff08\u4e0b\u884c\uff09")));
        }

        return rules;
    }

    private IReadOnlyList<MaintenanceWindowRule> LoadMaintenanceWindows(string datasetDirectory)
    {
        string path = Path.Combine(datasetDirectory, "\u5929\u7a97.csv");
        return _csvReader.ReadRows(path)
            .Select(row => new MaintenanceWindowRule(
                GetRequired(row, "\u533a\u95f4\u540d\u79f0"),
                GetRequired(row, "\u533a\u95f4\u524d\u7ad9"),
                ParseYesNo(row, "\u662f\u5426\u5305\u542b\u524d\u7ad9"),
                GetRequired(row, "\u533a\u95f4\u540e\u7ad9"),
                ParseYesNo(row, "\u662f\u5426\u5305\u542b\u540e\u7ad9"),
                ParseDirection(GetRequired(row, "\u5929\u7a97\u884c\u522b")),
                ParseRequiredInt(row, "\u5f00\u59cb\u65f6\u95f4"),
                ParseRequiredInt(row, "\u7ed3\u675f\u65f6\u95f4")))
            .ToList();
    }

    /// <summary>Load station quotas and verify their station references against the dataset.</summary>
    /// <param name="datasetDirectory">Directory containing the normalized CSV tables.</param>
    /// <param name="stations">Known station names from the dataset.</param>
    /// <returns>Quota rules in data-row order. The table itself is optional.</returns>
    /// <exception cref="InvalidDataException">A quota row is invalid or references an unknown station.</exception>
    private IReadOnlyList<StationStopQuotaRule> LoadStationStopQuotaRules(string datasetDirectory, IReadOnlyList<string> stations)
    {
        string path = Path.Combine(datasetDirectory, "\u8f66\u7ad9\u65f6\u6bb5\u505c\u7ad9\u6b21\u6570.csv");
        if (!File.Exists(path))
            return Array.Empty<StationStopQuotaRule>();

        var stationNamesSet = stations.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _csvReader.ReadRows(path)
            .Select((row, ruleIndex) => ParseStationStopQuotaRule(row, ruleIndex + 1, stationNamesSet))
            .ToList();
    }

    /// <summary>Parse a required station quota row with its data-row identity.</summary>
    /// <param name="row">Parsed CSV fields with the original column names.</param>
    /// <param name="ruleNumber">One-based ordinal among data rows.</param>
    /// <param name="stationNamesSet">Known station names with case-insensitive lookup.</param>
    /// <returns>Station, train type and closed quota window in seconds with integer count bounds.</returns>
    /// <exception cref="InvalidDataException">A field is missing or invalid, or the station reference is unknown.</exception>
    internal static StationStopQuotaRule ParseStationStopQuotaRule(IReadOnlyDictionary<string, string> row,
        int ruleNumber, IReadOnlySet<string> stationNamesSet)
    {
        string identity = $"station-stop-{ruleNumber:D3}";
        StationStopQuotaRule rule;
        try
        {
            rule = new StationStopQuotaRule(
                GetRequired(row, "\u8f66\u7ad9\u540d\u79f0"),
                GetRequired(row, "\u5217\u8f66\u79cd\u7c7b"),
                ParseRequiredInt(row, "\u65f6\u6bb5\u8d77\u70b9"),
                ParseRequiredInt(row, "\u65f6\u6bb5\u7ec8\u70b9"),
                ParseRequiredInt(row, "\u6700\u5c0f\u505c\u7ad9\u6b21\u6570"),
                ParseRequiredInt(row, "\u6700\u5927\u505c\u7ad9\u6b21\u6570"));
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException($"Quota '{identity}': {exception.Message}", exception);
        }

        if (!stationNamesSet.Contains(rule.StationName))
            throw new InvalidDataException($"Quota '{identity}' has unknown station reference '{rule.StationName}'.");
        if (rule.MinStops < 0 || rule.MaxStops < rule.MinStops || rule.StartTimeSeconds < 0 ||
            rule.StartTimeSeconds > rule.EndTimeSeconds)
            throw new InvalidDataException($"Quota '{identity}' has invalid bounds or window: {rule}.");
        return rule;
    }

    private IReadOnlyList<TrainSectionStopQuotaRule> LoadTrainSectionStopQuotaRules(string datasetDirectory)
    {
        string path = Path.Combine(datasetDirectory, "\u5217\u8f66\u533a\u6bb5\u505c\u7ad9\u6b21\u6570.csv");
        if (!File.Exists(path))
        {
            return Array.Empty<TrainSectionStopQuotaRule>();
        }

        return _csvReader.ReadRows(path)
            .Select(row => new TrainSectionStopQuotaRule(
                ParseRequiredInt(row, "\u5217\u8f66\u5e8f\u53f7"),
                GetRequired(row, "\u533a\u6bb5\u59cb\u7ad9"),
                GetRequired(row, "\u533a\u6bb5\u7ec8\u7ad9"),
                ParseRequiredInt(row, "\u6700\u5c0f\u505c\u7ad9\u6b21\u6570"),
                ParseRequiredInt(row, "\u6700\u5927\u505c\u7ad9\u6b21\u6570"),
                ParseYesNo(row, "\u662f\u5426\u7edf\u8ba1\u59cb\u7ad9"),
                ParseYesNo(row, "\u662f\u5426\u7edf\u8ba1\u7ec8\u7ad9")))
            .ToList();
    }

    private IReadOnlyList<TrainSectionTravelTimeLimitRule> LoadTrainSectionTravelTimeLimitRules(string datasetDirectory)
    {
        string path = Path.Combine(datasetDirectory, "\u5217\u8f66\u533a\u6bb5\u65c5\u884c\u65f6\u95f4\u8981\u6c42.csv");
        if (!File.Exists(path))
        {
            return Array.Empty<TrainSectionTravelTimeLimitRule>();
        }

        List<TrainSectionTravelTimeLimitRule> result = new();
        foreach (IGrouping<string, TrainSectionTravelTimeLimitRule> group in _csvReader.ReadRows(path)
            .Select(row => new TrainSectionTravelTimeLimitRule(
                ParseRequiredInt(row, "\u5217\u8f66\u5e8f\u53f7"),
                GetRequired(row, "\u533a\u6bb5\u59cb\u7ad9"),
                GetRequired(row, "\u533a\u6bb5\u7ec8\u7ad9"),
                ParseRequiredInt(row, "\u6700\u5927\u65c5\u884c\u65f6\u95f4")))
            .GroupBy(rule => $"{rule.TrainIndex}::{rule.SectionStartStation}::{rule.SectionEndStation}", StringComparer.OrdinalIgnoreCase))
        {
            List<int> limits = group.Select(rule => rule.MaxTravelTimeSeconds).Distinct().ToList();
            if (limits.Count != 1)
            {
                throw new InvalidDataException($"Conflicting maximum travel-time limits for '{group.Key}'.");
            }

            result.Add(group.First());
        }

        return result;
    }

    private IReadOnlyList<BenchmarkTrackRestriction> LoadBenchmarkTrackRestrictions(string datasetDirectory)
    {
        string path = Path.Combine(datasetDirectory, "\u5217\u8f66\u7279\u6b8a\u4f5c\u4e1a\u80a1\u9053\u9650\u5236.csv");
        if (!File.Exists(path))
        {
            return Array.Empty<BenchmarkTrackRestriction>();
        }

        List<BenchmarkTrackRestriction> result = new();
        foreach (IGrouping<string, BenchmarkTrackRestriction> group in _csvReader.ReadRows(path)
            .Select(row => new BenchmarkTrackRestriction(
                ParseRequiredInt(row, "\u5217\u8f66\u5e8f\u53f7"),
                ParseRequiredInt(row, "\u7ad9\u987a"),
                GetRequiredAny(row, "\u8f66\u7ad9\u540d\u79f0", "\u8f66\u7ad9"),
                SplitTrackSet(NullIfEmpty(row, "\u9650\u5236\u80a1\u9053")).ToList()))
            .GroupBy(item => $"{item.TrainIndex}::{item.StationOrder}", StringComparer.OrdinalIgnoreCase))
        {
            List<BenchmarkTrackRestriction> rows = group.ToList();
            if (rows.Any(row => row.AllowedTrackNames.Count == 0))
            {
                throw new InvalidDataException($"Benchmark track restriction '{group.Key}' has an empty track set.");
            }

            string stationName = rows[0].StationName;
            if (rows.Any(row => !string.Equals(row.StationName, stationName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException($"Conflicting station names for benchmark track restriction '{group.Key}'.");
            }

            IReadOnlyList<string> allowedTracks = rows
                .SelectMany(row => row.AllowedTrackNames)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            result.Add(new BenchmarkTrackRestriction(rows[0].TrainIndex, rows[0].StationOrder, stationName, allowedTracks));
        }

        return result;
    }

    private IReadOnlyList<RouteRecord> LoadRoutes(string datasetDirectory)
    {
        string path = Path.Combine(datasetDirectory, "\u8fdb\u8def.csv");
        if (!File.Exists(path))
        {
            return Array.Empty<RouteRecord>();
        }

        return _csvReader.ReadRows(path)
            .Select(row =>
            {
                string operationType = GetRequired(row, "\u4f5c\u4e1a\u7c7b\u578b");
                return new RouteRecord(
                    ParseRequiredInt(row, "\u8fdb\u8def\u5e8f\u53f7"),
                    GetRequired(row, "\u8f66\u7ad9\u540d\u79f0"),
                    GetRequired(row, "\u533a\u95f4\u540d\u79f0"),
                    GetRequired(row, "\u80a1\u9053\u540d\u79f0"),
                    operationType,
                    ParseMovementEvent(operationType));
            })
            .ToList();
    }

    private IReadOnlyList<MovementHeadwayRule> LoadMovementHeadwayRules(
        string datasetDirectory,
        IReadOnlyList<RouteRecord> routes)
    {
        string path = Path.Combine(datasetDirectory, "\u95f4\u9694\u65f6\u95f4.csv");
        if (!File.Exists(path) || routes.Count == 0)
        {
            return Array.Empty<MovementHeadwayRule>();
        }

        const int safetyMarginSeconds = 30;
        Dictionary<int, RouteRecord> routeById = routes.ToDictionary(route => route.RouteId);
        Dictionary<(string StationName, MovementEvent Leading, MovementEvent Following), List<int>> grouped = new();

        foreach (IReadOnlyDictionary<string, string> row in _csvReader.ReadRows(path))
        {
            int leadingRouteId = ParseRequiredInt(row, "\u524d\u8f66\u8fdb\u8def\u5e8f\u53f7");
            int followingRouteId = ParseRequiredInt(row, "\u540e\u8f66\u8fdb\u8def\u5e8f\u53f7");
            int headwaySeconds = ParseRequiredInt(row, "\u6700\u5c0f\u95f4\u9694\u65f6\u95f4");

            if (!routeById.TryGetValue(leadingRouteId, out RouteRecord? leadingRoute) ||
                !routeById.TryGetValue(followingRouteId, out RouteRecord? followingRoute))
            {
                continue;
            }

            // 当前版本将代理间隔放宽成“同站级 family 对”：
            // 只要前后精确进路属于同一车站，就参与同站级模式聚合，不再要求必须同一区间。
            if (!string.Equals(leadingRoute.StationName, followingRoute.StationName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (leadingRoute.MovementEvent == MovementEvent.Unknown ||
                followingRoute.MovementEvent == MovementEvent.Unknown)
            {
                continue;
            }

            var key = (leadingRoute.StationName, leadingRoute.MovementEvent, followingRoute.MovementEvent);
            if (!grouped.TryGetValue(key, out List<int>? values))
            {
                values = new List<int>();
                grouped[key] = values;
            }

            values.Add(headwaySeconds);
        }

        return grouped
            .Select(group =>
            {
                int modeHeadwaySeconds = ResolveModeWithSafeTieBreak(group.Value);
                return new MovementHeadwayRule(
                    group.Key.StationName,
                    group.Key.Leading,
                    group.Key.Following,
                    modeHeadwaySeconds + safetyMarginSeconds,
                    modeHeadwaySeconds,
                    safetyMarginSeconds,
                    group.Value.Count);
            })
            .OrderBy(rule => rule.StationName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(rule => rule.LeadingEvent)
            .ThenBy(rule => rule.FollowingEvent)
            .ToList();
    }

    private IReadOnlyList<RouteHeadwayRule> LoadRouteHeadwayRules(string datasetDirectory)
    {
        string path = Path.Combine(datasetDirectory, "\u95f4\u9694\u65f6\u95f4.csv");
        if (!File.Exists(path))
        {
            return Array.Empty<RouteHeadwayRule>();
        }

        return _csvReader.ReadRows(path)
            .Select(row => new RouteHeadwayRule(
                ParseRequiredInt(row, "\u524d\u8f66\u8fdb\u8def\u5e8f\u53f7"),
                ParseRequiredInt(row, "\u540e\u8f66\u8fdb\u8def\u5e8f\u53f7"),
                ParseRequiredInt(row, "\u6700\u5c0f\u95f4\u9694\u65f6\u95f4")))
            .ToList();
    }

    private IReadOnlyList<TrainTrackChoiceRecord> LoadTrainTrackChoiceRecords(string datasetDirectory)
    {
        List<TrainTrackChoiceRecord> records = new();
        records.AddRange(LoadTrainTrackChoiceRecords(Path.Combine(datasetDirectory, "\u5217\u8f66\u505c\u7ad9\u80a1\u9053.csv"), isStoppingChoice: true));
        records.AddRange(LoadTrainTrackChoiceRecords(Path.Combine(datasetDirectory, "\u5217\u8f66\u901a\u8fc7\u80a1\u9053.csv"), isStoppingChoice: false));
        return records;
    }

    private IReadOnlyList<TrainTrackChoiceRecord> LoadTrainTrackChoiceRecords(string path, bool isStoppingChoice)
    {
        if (!File.Exists(path))
        {
            return Array.Empty<TrainTrackChoiceRecord>();
        }

        return _csvReader.ReadRows(path)
            .Select(row => new TrainTrackChoiceRecord(
                ParseRequiredInt(row, "\u5217\u8f66\u5e8f\u53f7"),
                ParseRequiredInt(row, "\u7ad9\u987a"),
                GetRequired(row, "\u8f66\u7ad9\u540d\u79f0"),
                isStoppingChoice,
                SplitTrackSet(NullIfEmpty(row, "\u80a1\u9053\u96c6\u5408")).ToList()))
            .ToList();
    }

    private IReadOnlyList<StationOvertakingProfile> LoadStationOvertakingProfiles(
        string datasetDirectory,
        IReadOnlyList<string> stations,
        IReadOnlyList<RouteRecord> routes,
        IReadOnlyList<TrainStopRecord> stops)
    {
        Dictionary<string, HashSet<string>> tracksByStation = LoadTrackSetsByStation(
            Path.Combine(datasetDirectory, "\u80a1\u9053.csv"),
            "\u80a1\u9053\u540d\u79f0");

        Dictionary<string, HashSet<string>> stopTracksByStation = LoadRequestTrackSetsByStation(
            Path.Combine(datasetDirectory, "\u5217\u8f66\u505c\u7ad9\u80a1\u9053.csv"));

        Dictionary<string, HashSet<string>> passTracksByStation = LoadRequestTrackSetsByStation(
            Path.Combine(datasetDirectory, "\u5217\u8f66\u901a\u8fc7\u80a1\u9053.csv"));

        int minStationOrder = stops.Select(stop => stop.StationOrder).DefaultIfEmpty(0).Min();
        int maxStationOrder = stops.Select(stop => stop.StationOrder).DefaultIfEmpty(0).Max();
        HashSet<string> boundaryStations = stops
            .Where(stop => stop.StationOrder == minStationOrder || stop.StationOrder == maxStationOrder)
            .Select(stop => stop.StationName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        HashSet<string> allStations = new(stations, StringComparer.OrdinalIgnoreCase);
        allStations.UnionWith(routes.Select(route => route.StationName));
        allStations.UnionWith(stops.Select(stop => stop.StationName));

        List<StationOvertakingProfile> profiles = new();
        foreach (string stationName in allStations.OrderBy(station => station, StringComparer.OrdinalIgnoreCase))
        {
            List<RouteRecord> stationRoutes = routes
                .Where(route => string.Equals(route.StationName, stationName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            List<TrainStopRecord> stationStops = stops
                .Where(stop => string.Equals(stop.StationName, stationName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            int trackCount = tracksByStation.TryGetValue(stationName, out HashSet<string>? stationTracks) ? stationTracks.Count : 0;
            int routeTrackCount = stationRoutes
                .Select(route => route.TrackName)
                .Where(track => !string.IsNullOrWhiteSpace(track))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            bool hasDepartureEvent = stationRoutes.Any(route => route.MovementEvent == MovementEvent.Departure);
            bool hasPassingEvent = stationRoutes.Any(route => route.MovementEvent == MovementEvent.Passing);
            bool hasStopTrackEvidence = stopTracksByStation.TryGetValue(stationName, out HashSet<string>? stopTracks) && stopTracks.Count > 0;
            bool hasPassTrackEvidence = passTracksByStation.TryGetValue(stationName, out HashSet<string>? passTracks) && passTracks.Count > 0;
            int mustStopCount = stationStops.Count(stop => stop.StopRequirement == StopRequirement.MustStop);
            int optionalStopCount = stationStops.Count(stop => stop.StopRequirement == StopRequirement.OptionalStop);
            int mustPassCount = stationStops.Count(stop => stop.StopRequirement == StopRequirement.MustPass);

            int score = 0;
            if (trackCount >= 3)
            {
                score++;
            }

            if (routeTrackCount >= 2)
            {
                score++;
            }

            if (hasDepartureEvent && hasPassingEvent)
            {
                score += 2;
            }

            if (hasStopTrackEvidence && hasPassTrackEvidence)
            {
                score++;
            }

            if (optionalStopCount > 0 || (mustStopCount > 0 && mustPassCount > 0))
            {
                score++;
            }

            bool isBoundaryStation = boundaryStations.Contains(stationName);
            bool isOvertakingCapable = !isBoundaryStation && score >= 3;

            profiles.Add(new StationOvertakingProfile(
                stationName,
                isBoundaryStation,
                score,
                isOvertakingCapable,
                trackCount,
                routeTrackCount,
                hasDepartureEvent,
                hasPassingEvent,
                hasStopTrackEvidence,
                hasPassTrackEvidence,
                mustStopCount,
                optionalStopCount,
                mustPassCount));
        }

        return profiles;
    }

    private Dictionary<string, HashSet<string>> LoadTrackSetsByStation(string path, string trackColumnName)
    {
        Dictionary<string, HashSet<string>> result = new(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return result;
        }

        foreach (IReadOnlyDictionary<string, string> row in _csvReader.ReadRows(path))
        {
            string stationName = GetRequiredAny(row, "\u8f66\u7ad9\u540d\u79f0", "\u8f66\u7ad9\u7f16\u53f7");
            string trackName = GetRequired(row, trackColumnName);
            if (!result.TryGetValue(stationName, out HashSet<string>? tracks))
            {
                tracks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                result[stationName] = tracks;
            }

            tracks.Add(trackName);
        }

        return result;
    }

    private Dictionary<string, HashSet<string>> LoadRequestTrackSetsByStation(string path)
    {
        Dictionary<string, HashSet<string>> result = new(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return result;
        }

        foreach (IReadOnlyDictionary<string, string> row in _csvReader.ReadRows(path))
        {
            string stationName = GetRequired(row, "\u8f66\u7ad9\u540d\u79f0");
            if (!result.TryGetValue(stationName, out HashSet<string>? tracks))
            {
                tracks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                result[stationName] = tracks;
            }

            foreach (string trackName in SplitTrackSet(NullIfEmpty(row, "\u80a1\u9053\u96c6\u5408")))
            {
                tracks.Add(trackName);
            }
        }

        return result;
    }

    private (IReadOnlyList<TrainRecord> Trains, IReadOnlyList<TrainStopRecord> Stops) LoadTrains(string datasetDirectory)
    {
        string path = Path.Combine(datasetDirectory, "\u5217\u8f66.csv");
        IReadOnlyList<IReadOnlyDictionary<string, string>> rows = _csvReader.ReadRows(path);

        List<TrainStopRecord> rawStops = rows
            .Select(CreateStopRecord)
            .OrderBy(stop => stop.TrainIndex)
            .ThenBy(stop => stop.StationOrder)
            .ToList();

        List<TrainRecord> trains = new();
        List<TrainStopRecord> finalizedStops = new();

        foreach (IGrouping<(int TrainIndex, string TrainNo, string TrainType), TrainStopRecord> group in rawStops.GroupBy(stop => (stop.TrainIndex, stop.TrainNo, stop.TrainType)))
        {
            List<TrainStopRecord> orderedStops = group.OrderBy(stop => stop.StationOrder).ToList();
            for (int index = 0; index < orderedStops.Count; index++)
            {
                TrainStopRole role = index switch
                {
                    0 => TrainStopRole.Origin,
                    var i when i == orderedStops.Count - 1 => TrainStopRole.Destination,
                    _ => TrainStopRole.Intermediate
                };

                orderedStops[index] = orderedStops[index] with { StopRole = role };
            }

            finalizedStops.AddRange(orderedStops);
            trains.Add(new TrainRecord(group.Key.TrainIndex, group.Key.TrainNo, group.Key.TrainType, orderedStops));
        }

        return (trains.OrderBy(train => train.TrainIndex).ToList(), finalizedStops);
    }

    private static TrainStopRecord CreateStopRecord(IReadOnlyDictionary<string, string> row)
    {
        int trainIndex = ParseRequiredInt(row, "\u5217\u8f66\u5e8f\u53f7");
        int stationOrder = ParseRequiredInt(row, "\u7ad9\u987a");
        string stationName = GetRequired(row, "\u8f66\u7ad9\u540d\u79f0");
        string trainNo = GetRequired(row, "\u8f66\u6b21");
        string trainType = GetRequired(row, "\u5217\u8f66\u79cd\u7c7b");

        return new TrainStopRecord(
            StopId: $"train-{trainIndex}-stop-{stationOrder}",
            TrainIndex: trainIndex,
            TrainNo: trainNo,
            TrainType: trainType,
            StationOrder: stationOrder,
            StationName: stationName,
            IncomingSectionName: NullIfEmpty(row, "\u533a\u95f4\u540d\u79f0"),
            RawArrTimeSeconds: ParseNullableTime(row, "\u5230\u70b9"),
            RawDepTimeSeconds: ParseNullableTime(row, "\u53d1\u70b9"),
            EarliestArrSeconds: ParseRequiredInt(row, "\u6700\u65e9\u5230\u8fbe\u65f6\u523b"),
            LatestArrSeconds: ParseRequiredInt(row, "\u6700\u665a\u5230\u8fbe\u65f6\u523b"),
            EarliestDepSeconds: ParseRequiredInt(row, "\u6700\u65e9\u51fa\u53d1\u65f6\u523b"),
            LatestDepSeconds: ParseRequiredInt(row, "\u6700\u665a\u51fa\u53d1\u65f6\u523b"),
            IdealStop: ParseBoolYesNo(row, "\u7406\u60f3\u505c\u7ad9"),
            StopDeviationWeight: ParseRequiredInt(row, "\u505c\u7ad9\u504f\u5dee\u6743\u91cd"),
            StopRequirement: ParseStopRequirement(GetRequired(row, "\u505c\u7ad9\u8981\u6c42")),
            MinDwellSeconds: ParseRequiredInt(row, "\u6700\u5c0f\u505c\u7ad9\u65f6\u95f4"),
            MaxDwellSeconds: ParseRequiredInt(row, "\u6700\u5927\u505c\u7ad9\u65f6\u95f4"),
            RunScaleName: NullIfEmpty(row, "\u8fd0\u884c\u6807\u5c3a"),
            SlowScaleName: NullIfEmpty(row, "\u6162\u884c\u6807\u5c3a"),
            // 大案例 JH 的始发站等记录可能没有慢行调整值；当前 master 尚未使用慢行调整，
            // 因此空值按 0 处理，避免非核心字段阻断数据加载。
            MinSlowAdjustmentSeconds: ParseOptionalInt(row, "\u6700\u5c0f\u6162\u884c\u8c03\u6574", 0),
            MaxSlowAdjustmentSeconds: ParseOptionalInt(row, "\u6700\u5927\u6162\u884c\u8c03\u6574", 0),
            LeftDepartureWeight: ParseOptionalInt(row, "\u51fa\u53d1\u5de6\u504f\u6743\u91cd", 0),
            RightDepartureWeight: ParseOptionalInt(row, "\u51fa\u53d1\u53f3\u504f\u6743\u91cd", 0),
            StopRole: TrainStopRole.Intermediate);
    }

    private static string GetRequired(IReadOnlyDictionary<string, string> row, string key)
    {
        if (!row.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"Required field '{key}' is missing or empty.");
        }

        return value.Trim();
    }

    private static string GetRequiredAny(IReadOnlyDictionary<string, string> row, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (row.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        throw new InvalidDataException($"None of the required fields is present or non-empty: {string.Join(", ", keys)}.");
    }

    private static string? NullIfEmpty(IReadOnlyDictionary<string, string> row, string key)
    {
        return row.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
    }

    private static IEnumerable<string> SplitTrackSet(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        return value
            .Split(new[] { ',', '\uff0c', ';', '\uff1b', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item));
    }

    private static int ParseRequiredInt(IReadOnlyDictionary<string, string> row, string key)
    {
        string value = GetRequired(row, key);
        if (!int.TryParse(value, out int parsed))
        {
            throw new InvalidDataException($"Field '{key}' is not a valid integer: '{value}'.");
        }

        return parsed;
    }

    private static int ParseOptionalInt(IReadOnlyDictionary<string, string> row, string key, int defaultValue)
    {
        if (!row.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return int.TryParse(value.Trim(), out int parsed) ? parsed : defaultValue;
    }

    private static int? ParseNullableTime(IReadOnlyDictionary<string, string> row, string key)
    {
        if (!row.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!int.TryParse(value.Trim(), out int parsed) || parsed < 0)
        {
            return null;
        }

        return parsed;
    }

    private static double? ParseNullableDouble(IReadOnlyDictionary<string, string> row, string key)
    {
        if (!row.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return double.TryParse(value.Trim(), out double parsed) ? parsed : null;
    }

    private static bool ParseYesNo(IReadOnlyDictionary<string, string> row, string key)
    {
        string value = GetRequired(row, key);
        return value switch
        {
            "\u662f" => true,
            "\u5426" => false,
            _ => throw new InvalidDataException($"Field '{key}' must be 是/否, actual value: '{value}'.")
        };
    }

    private static bool ParseBoolYesNo(IReadOnlyDictionary<string, string> row, string key)
    {
        if (!row.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Trim() switch
        {
            "\u662f" => true,
            "\u5426" => false,
            _ => false
        };
    }

    private static TravelDirection ParseDirection(string value)
    {
        return value.Trim() switch
        {
            "\u4e0a\u884c" => TravelDirection.Up,
            "\u4e0b\u884c" => TravelDirection.Down,
            _ => TravelDirection.Unknown
        };
    }

    private static StopRequirement ParseStopRequirement(string value)
    {
        return value.Trim() switch
        {
            "\u5fc5\u505c" => StopRequirement.MustStop,
            "\u9009\u505c" => StopRequirement.OptionalStop,
            "\u7981\u505c" => StopRequirement.MustPass,
            _ => throw new InvalidDataException($"Unsupported stop requirement: '{value}'.")
        };
    }

    private static MovementEvent ParseMovementEvent(string value)
    {
        return value.Trim() switch
        {
            "\u63a5\u8f66" => MovementEvent.Arrival,
            "\u53d1\u8f66" => MovementEvent.Departure,
            "\u901a\u8fc7\u63a5\u8f66" => MovementEvent.Passing,
            "\u901a\u8fc7\u53d1\u8f66" => MovementEvent.Passing,
            _ => MovementEvent.Unknown
        };
    }

    private static int ResolveModeWithSafeTieBreak(IEnumerable<int> values)
    {
        return values
            .GroupBy(value => value)
            .OrderByDescending(group => group.Count())
            .ThenByDescending(group => group.Key)
            .Select(group => group.Key)
            .First();
    }
}
