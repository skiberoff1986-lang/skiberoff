namespace EquipmentDowntime.Core;

public static class RecordRules
{
    public static readonly DateTime MinDate = new(1900, 1, 1);
    // WinForms DateTimePicker rejects a maximum later than 31.12.9998 00:00.
    public static readonly DateTime MaxDate = new(9998, 12, 31);

    /// <summary>Требование для нового завершения, без изменения старых закрытых заявок.</summary>
    public static IReadOnlyList<string> ValidateNewStages(DowntimeRecord? saved, DowntimeRecord proposed) =>
        saved?.RestoredAt is null && proposed.RestoredAt.HasValue && string.IsNullOrWhiteSpace(proposed.WorkDescription)
            ? ["При выдаче крана из ремонта заполните описание выполненных работ."]
            : [];

    /// <summary>Сохранённые значения неизменяемы; открытый простой можно только дополнить.</summary>
    public static IReadOnlyList<string> ValidateUpdate(DowntimeRecord saved, DowntimeRecord proposed)
    {
        var errors = new List<string>();
        if (saved.Id != proposed.Id)
            errors.Add("Нельзя менять идентификатор зарегистрированной записи.");
        if (saved.RestoredAt.HasValue)
        {
            if ((saved with { WorksWithRestrictions = proposed.WorksWithRestrictions }) != proposed)
                errors.Add("Простой уже завершён. Его данные и время устранения изменять нельзя.");
            return errors;
        }
        if (saved.ReportedAt != proposed.ReportedAt)
            errors.Add("Дата и время регистрации уже зафиксированы.");
        CheckText("№ цеха", saved.ShopNumber, proposed.ShopNumber);
        CheckText("№ крана", saved.CraneNumber, proposed.CraneNumber);
        CheckText("Фамилия И.О.", saved.ReporterName, proposed.ReporterName);
        CheckText("Описание неисправности", saved.FaultDescription, proposed.FaultDescription);
        CheckText("Описание выполненных работ", saved.WorkDescription, proposed.WorkDescription);
        CheckText("Фамилия ответственного исполнителя", saved.ResponsiblePerson, proposed.ResponsiblePerson);
        CheckText("Мастер смены", saved.ShiftMaster, proposed.ShiftMaster);
        CheckTime("Время реагирования", saved.RespondedAt, proposed.RespondedAt);
        CheckTime("Время выдачи крана в ремонт", saved.RepairStartedAt, proposed.RepairStartedAt);
        return errors;

        void CheckText(string field, string oldValue, string newValue)
        {
            if (!string.IsNullOrWhiteSpace(oldValue) &&
                !string.Equals(oldValue, newValue, StringComparison.Ordinal))
                errors.Add($"«{field}» уже сохранено и не может быть изменено или очищено.");
        }
        void CheckTime(string field, DateTime? oldValue, DateTime? newValue)
        {
            if (oldValue.HasValue && oldValue != newValue)
                errors.Add($"«{field}» уже зарегистрировано и не может быть изменено или очищено.");
        }
    }

    public static IReadOnlyList<string> Validate(DowntimeRecord record, DateTime? now = null)
    {
        var errors = new List<string>();
        if (record.Id == Guid.Empty)
            errors.Add("Отсутствует идентификатор записи.");
        if (string.IsNullOrWhiteSpace(record.ReporterName))
            errors.Add("Заполните поле «Фамилия И.О.».");
        if (string.IsNullOrWhiteSpace(record.FaultDescription))
            errors.Add("Заполните описание неисправности.");
        if (record.ShopNumber is null || record.CraneNumber is null ||
            record.ReporterName is null || record.ResponsiblePerson is null ||
            record.ShiftMaster is null || record.FaultDescription is null || record.WorkDescription is null)
            errors.Add("Текстовые поля не должны содержать null.");
        if ((record.ShopNumber?.Length ?? 0) > 40 || (record.CraneNumber?.Length ?? 0) > 40)
            errors.Add("Номер цеха и номер крана: не более 40 символов.");
        if ((record.ReporterName?.Length ?? 0) > 120 ||
            (record.ResponsiblePerson?.Length ?? 0) > 120 ||
            (record.ShiftMaster?.Length ?? 0) > 120)
            errors.Add("Фамилия и инициалы: не более 120 символов.");
        if ((record.FaultDescription?.Length ?? 0) > 4000)
            errors.Add("Описание: не более 4000 символов.");
        if ((record.WorkDescription?.Length ?? 0) > 4000)
            errors.Add("Описание выполненных работ: не более 4000 символов.");

        (string Name, DateTime? Value)[] events =
        [
            ("Регистрация", record.ReportedAt),
            ("Реагирование", record.RespondedAt),
            ("Выдача крана в ремонт", record.RepairStartedAt),
            ("Устранение неисправности", record.RestoredAt)
        ];
        DateTime? previous = null;
        string previousName = "";
        foreach (var (name, nullableValue) in events)
        {
            if (nullableValue is not DateTime value) continue;
            if (value < MinDate || value > MaxDate)
                errors.Add($"{name}: дата должна быть между 01.01.1900 и 31.12.9998.");
            if (value != TimeText.ToMinute(value) || value.Kind != DateTimeKind.Unspecified)
                errors.Add($"{name}: требуется местное время с точностью до минуты.");
            if (previous.HasValue && value < previous.Value)
                errors.Add($"«{name}» не может быть раньше события «{previousName}».");
            if (now.HasValue && value > TimeText.ToMinute(now.Value))
                errors.Add($"«{name}» не может находиться в будущем.");
            previous = value;
            previousName = name;
        }
        return errors;
    }
}
