// 股道占用区间采用中间站 [到达,出发]；始发、终到和通过按事件点处理，不额外增加出清时间。
// 统一的区间语义同时供微观检查器与车站子问题使用，避免同一事件在不同校验路径中出现边界差异。
using System.Globalization;
using GurobiTimetablingResearch.Domain;

namespace GurobiTimetablingResearch.Optimization;

public sealed record StationOccupationInterval(double StartSeconds, double EndSeconds);

public static class StationEndpointOccupationPolicy
{
    // Endpoint preparation/storage is outside the timetable model. Positive values
    // are explicit legacy replay options, never an implicit clearance allowance.
    public const int DefaultSeconds = 0;
    public static StationOccupationInterval Resolve(
        TrainStopRole role,
        bool isStop,
        double arrivalSeconds,
        double departureSeconds,
        int endpointOccupationSeconds,
        double domainLowerBound,
        double domainUpperBound)
    {
        if (endpointOccupationSeconds < 0 || endpointOccupationSeconds > 86400)
        {
            throw new ArgumentOutOfRangeException(nameof(endpointOccupationSeconds),
                "Endpoint occupation seconds must be nonnegative and no greater than 86400.");
        }

        if (domainLowerBound > domainUpperBound)
        {
            throw new ArgumentException("The station occupation domain lower bound cannot exceed its upper bound.");
        }

        if (!isStop)
        {
            double passingTime = role == TrainStopRole.Origin ? departureSeconds : arrivalSeconds;
            return new StationOccupationInterval(
                Math.Clamp(passingTime, domainLowerBound, domainUpperBound),
                Math.Clamp(passingTime, domainLowerBound, domainUpperBound));
        }

        return role switch
        {
            TrainStopRole.Origin => new StationOccupationInterval(
                departureSeconds - endpointOccupationSeconds, departureSeconds),
            TrainStopRole.Destination => new StationOccupationInterval(
                arrivalSeconds, arrivalSeconds + endpointOccupationSeconds),
            _ => new StationOccupationInterval(arrivalSeconds, departureSeconds)
        };
    }

    public static int ParseSeconds(string rawValue, string argumentName)
    {
        if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds) ||
            seconds < 0 || seconds > 86400)
        {
            throw new ArgumentException(
                $"{argumentName} must be a whole number of seconds from zero to 86400.",
                argumentName);
        }

        return seconds;
    }
}

public static class CheckerEndpointOccupationArgumentParser
{
    public static int Parse(IReadOnlyList<string> args, string prefix)
    {
        string? raw = args.FirstOrDefault(argument => argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (raw is null)
        {
            return StationEndpointOccupationPolicy.DefaultSeconds;
        }

        return StationEndpointOccupationPolicy.ParseSeconds(raw[prefix.Length..].Trim(), prefix);
    }
}
