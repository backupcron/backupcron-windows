using System.Globalization;

namespace BackupCron.Core;

public static class Scheduling
{
    public static bool IsDue(BackupJob job, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        if (!job.IsEnabled || job.Schedule == ScheduleKind.Manual) return false;
        var anchor = job.LastRunAt ?? job.CreatedAt;
        if (job.Schedule is ScheduleKind.Interval or ScheduleKind.Hourly) return now - anchor >= Interval(job);
        return LastSlot(job, now, zone ?? TimeZoneInfo.Local) is { } slot && anchor < slot;
    }
    public static DateTimeOffset? NextRun(BackupJob job, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        if (!job.IsEnabled || job.Schedule == ScheduleKind.Manual) return null;
        if (job.Schedule is ScheduleKind.Interval or ScheduleKind.Hourly)
        {
            var anchor = job.LastRunAt ?? job.CreatedAt; var step = Interval(job);
            return anchor + TimeSpan.FromTicks(step.Ticks * Math.Max(1, (long)Math.Floor((now - anchor).Ticks / (double)step.Ticks) + 1));
        }
        zone ??= TimeZoneInfo.Local; var date = TimeZoneInfo.ConvertTime(now, zone).Date;
        for (int days = 0; days <= 8; days++)
        {
            var candidate = Slot(job, date.AddDays(days), zone);
            if (candidate != null && candidate > now) return candidate;
        }
        return null;
    }
    private static TimeSpan Interval(BackupJob j) => TimeSpan.FromMinutes(j.Schedule == ScheduleKind.Hourly ? 60 : Math.Clamp(j.IntervalMinutes, 5, 1440));
    private static DateTimeOffset? LastSlot(BackupJob job, DateTimeOffset now, TimeZoneInfo zone)
    {
        var date = TimeZoneInfo.ConvertTime(now, zone).Date;
        for (int days = 0; days <= 8; days++) { var slot = Slot(job, date.AddDays(-days), zone); if (slot != null && slot <= now) return slot; }
        return null;
    }
    private static DateTimeOffset? Slot(BackupJob job, DateTime date, TimeZoneInfo zone)
    {
        if (job.Schedule == ScheduleKind.Weekly && (int)date.DayOfWeek + 1 != Math.Clamp(job.Weekday, 1, 7)) return null;
        if (!TimeOnly.TryParseExact(job.ScheduledTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) return null;
        var local = DateTime.SpecifyKind(date + time.ToTimeSpan(), DateTimeKind.Unspecified);
        // Spring gaps advance to the next valid minute; fall overlaps use the first occurrence.
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }
}
