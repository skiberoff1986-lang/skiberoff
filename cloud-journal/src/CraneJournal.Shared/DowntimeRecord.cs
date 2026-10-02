namespace EquipmentDowntime.Core;

/// <summary>Локальные дата и время с точностью до минуты.</summary>
public sealed record DowntimeRecord
{
    public Guid Id { get; init; }
    public DateTime ReportedAt { get; init; }
    // Строки сохраняют ведущие нули и буквенные обозначения, например 01 и 4А.
    public string ShopNumber { get; init; } = "";
    public string CraneNumber { get; init; } = "";
    public string ReporterName { get; init; } = "";
    public string FaultDescription { get; init; } = "";
    public DateTime? RespondedAt { get; init; }
    public string ResponsiblePerson { get; init; } = "";
    public string ShiftMaster { get; init; } = "";
    public DateTime? RepairStartedAt { get; init; }
    public DateTime? RestoredAt { get; init; }
    public string WorkDescription { get; init; } = "";
    public bool WorksWithRestrictions { get; init; }

    // Фактическая длительность остаётся в истории; меняется только её учёт.
    public TimeSpan CountedDowntimeAt(DateTime now) =>
        WorksWithRestrictions ? TimeSpan.Zero : DowntimeAt(now);

    public TimeSpan DowntimeAt(DateTime now)
    {
        var end = RestoredAt ?? TimeText.ToMinute(now);
        if (end <= ReportedAt) return TimeSpan.Zero;

        var duration = end - ReportedAt;
        // После реагирования ожидание выдачи в ремонт исключается из простоя.
        // У открытой записи этот период продолжается до текущего момента.
        // У старой завершённой записи без выдачи в ремонт вычет неизвестен.
        DateTime? waitingEnd = RepairStartedAt ?? (RestoredAt.HasValue ? null : end);
        if (RespondedAt is DateTime responded && waitingEnd is DateTime issued)
        {
            // Ограничение интервалом простоя защищает предварительный расчёт
            // в форме, пока пользователь ещё вводит и проверяет даты.
            var start = responded > ReportedAt ? responded : ReportedAt;
            var stop = issued < end ? issued : end;
            if (stop > start) duration -= stop - start;
        }
        return duration;
    }
}

public static class TimeText
{
    public static DateTime ToMinute(DateTime value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0,
            DateTimeKind.Unspecified);

    // TotalHours, а не Hours: 26:15 не превращается в 02:15.
    public static string Duration(TimeSpan value) =>
        $"{(long)value.TotalHours:00}:{value.Minutes:00}";

    public static string DurationWords(TimeSpan value) =>
        $"{(long)value.TotalHours} ч {value.Minutes:00} мин";
}
