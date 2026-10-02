namespace EquipmentDowntime.Core;

public enum MaxDeliveryState { Pending, Sending, Sent, Blocked, Uncertain, Cancelled, Uploading }

// Registered = 0 сохраняет смысл уведомлений в журналах версии 3 без поля Stage.
public enum MaxNotificationStage { Registered, Responded, RepairStarted, Restored, AccountingChanged, Media }

public sealed record MaxNotification
{
    public Guid NotificationId { get; init; }
    public Guid? AttachmentId { get; init; }
    public string MediaToken { get; init; } = "";
    public Guid RecordId { get; init; }
    public MaxNotificationStage Stage { get; init; }
    public long ChatId { get; init; }
    public string Text { get; init; } = "";
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset NextAttemptAtUtc { get; init; }
    public DateTimeOffset? SentAtUtc { get; init; }
    public MaxDeliveryState State { get; init; }
    public int Attempts { get; init; }
    public string MessageId { get; init; } = "";
    public string Error { get; init; } = "";

    public static MaxNotification Create(DowntimeRecord record, long chatId, DateTimeOffset now,
        MaxNotificationStage stage = MaxNotificationStage.Registered) => new()
    {
        NotificationId = Guid.NewGuid(), RecordId = record.Id, Stage = stage, ChatId = chatId, Text = Compose(record, stage),
        CreatedAtUtc = now, NextAttemptAtUtc = now, State = MaxDeliveryState.Pending
    };

    public static MaxNotification ForMedia(DowntimeRecord record, JournalAttachment attachment, long chatId,
        DateTimeOffset now) => new()
    {
        NotificationId = Guid.NewGuid(), RecordId = record.Id, AttachmentId = attachment.Id,
        Stage = MaxNotificationStage.Media, ChatId = chatId,
        Text = $"{(attachment.Kind == MediaKind.Image ? "Фото" : "Видео")} неисправности\n" +
            $"Цех: {record.ShopNumber}\nКран: {record.CraneNumber}\nID заявки: {record.Id}\n" +
            $"Файл: {attachment.OriginalFileName}\nНеисправность: {Shorten(record.FaultDescription, 500)}",
        CreatedAtUtc = now, NextAttemptAtUtc = now
    };

    public static MaxNotification ForAccounting(DowntimeRecord record, long chatId, DateTimeOffset now, DateTime? localNow = null) => new()
    {
        NotificationId = Guid.NewGuid(), RecordId = record.Id, Stage = MaxNotificationStage.AccountingChanged,
        ChatId = chatId, CreatedAtUtc = now, NextAttemptAtUtc = now,
        Text = $"Изменён учёт простоя\nЦех: {record.ShopNumber}\nКран: {record.CraneNumber}\nID заявки: {record.Id}\n" +
            $"Работа с СДУ с ограничениями: {(record.WorksWithRestrictions ? "да" : "нет")}\n" +
            $"Расчётное время простоя: {TimeText.DurationWords(record.DowntimeAt(localNow ?? now.LocalDateTime))}\n" +
            $"В общий расчёт: {TimeText.DurationWords(record.CountedDowntimeAt(localNow ?? now.LocalDateTime))}\n" +
            (record.WorksWithRestrictions ? "Простой исключён из общей суммы." : "Простой снова учитывается в общей сумме.") +
            (record.RestoredAt.HasValue ? "" : "\nЗаявка открыта; длительность указана на момент изменения отметки.")
    };

    public static IEnumerable<MaxNotificationStage> NewStages(DowntimeRecord? previous, DowntimeRecord current)
    {
        if (previous is null) yield return MaxNotificationStage.Registered;
        if (previous?.RespondedAt is null && current.RespondedAt.HasValue)
            yield return MaxNotificationStage.Responded;
        if (previous?.RepairStartedAt is null && current.RepairStartedAt.HasValue)
            yield return MaxNotificationStage.RepairStarted;
        if (previous?.RestoredAt is null && current.RestoredAt.HasValue)
            yield return MaxNotificationStage.Restored;
    }

    public static string Compose(DowntimeRecord r, MaxNotificationStage stage = MaxNotificationStage.Registered)
    {
        var (title, label, at) = stage switch
        {
            MaxNotificationStage.Registered => ("Новая заявка на ремонт", "Регистрация", (DateTime?)r.ReportedAt),
            MaxNotificationStage.Responded => ("Реагирование на заявку", "Время реагирования", r.RespondedAt),
            MaxNotificationStage.RepairStarted => ("Кран выдан в ремонт", "Выдача в ремонт", r.RepairStartedAt),
            MaxNotificationStage.Restored => ("Кран выдан из ремонта", "Выдача из ремонта", r.RestoredAt),
            _ => throw new ArgumentOutOfRangeException(nameof(stage))
        };
        if (at is null) throw new ArgumentException("Время этапа не заполнено.", nameof(r));
        string header = $"{title}\nЦех: {r.ShopNumber}\nКран: {r.CraneNumber}\n" +
            $"{label}: {at.Value:dd.MM.yyyy HH:mm}\nID заявки: {r.Id}\n";
        if (stage == MaxNotificationStage.Registered) header += $"Заявитель: {r.ReporterName}\n";
        else
        {
            if (!string.IsNullOrWhiteSpace(r.ResponsiblePerson)) header += $"Ответственный: {r.ResponsiblePerson}\n";
            if (!string.IsNullOrWhiteSpace(r.ShiftMaster)) header += $"Мастер смены: {r.ShiftMaster}\n";
        }
        if (r.WorksWithRestrictions)
            header += "Работа с СДУ с ограничениями: да. Простой не учитывается в общей сумме.\n";
        if (stage == MaxNotificationStage.Restored)
        {
            // Тот же расчёт, что в журнале; время отправки не влияет на итог.
            header += $"Время простоя: {TimeText.DurationWords(r.DowntimeAt(at.Value))}\n";
            if (r.WorksWithRestrictions) header += "В общий расчёт: 0 ч 00 мин\n";
            header += r.RespondedAt is DateTime responded && r.RepairStartedAt is DateTime issued
                ? $"Исключено ожидание выдачи в ремонт: {TimeText.DurationWords(issued - responded)}\n"
                : "Период ожидания не указан — простой рассчитан без вычета.\n";
            header += "\nУстранение неисправности:\n";
            return header + Shorten(string.IsNullOrWhiteSpace(r.WorkDescription) ? "Описание не указано." : r.WorkDescription,
                Math.Min(1000, 4000 - header.Length));
        }
        header += "\nНеисправность:\n";
        return header + Shorten(r.FaultDescription,
            Math.Min(stage == MaxNotificationStage.Registered ? 4000 : 500, 4000 - header.Length));
    }

    private static string Shorten(string value, int limit)
    {
        if (value.Length <= limit) return value;
        const string suffix = "\n… Полное описание — в журнале.";
        int length = Math.Max(0, limit - suffix.Length);
        // Не разрываем суррогатную пару Unicode на границе сообщения.
        if (length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
        return value[..length] + suffix;
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public string StageText => Stage switch
    {
        MaxNotificationStage.Registered => "Регистрация заявки",
        MaxNotificationStage.Responded => "Реагирование",
        MaxNotificationStage.RepairStarted => "Выдача в ремонт",
        MaxNotificationStage.Restored => "Выдача из ремонта",
        MaxNotificationStage.AccountingChanged => "Учёт простоя / СДУ",
        MaxNotificationStage.Media => "Фото / видео",
        _ => "Неизвестный этап"
    };

    [System.Text.Json.Serialization.JsonIgnore]
    public string StateText => State switch
    {
        MaxDeliveryState.Pending => "Ожидает отправки",
        MaxDeliveryState.Sending => "Отправляется",
        MaxDeliveryState.Uploading => "Загружается файл",
        MaxDeliveryState.Sent => "Отправлено",
        MaxDeliveryState.Blocked => "Нужна настройка",
        MaxDeliveryState.Uncertain => "Проверьте группу",
        MaxDeliveryState.Cancelled => "Отменено",
        _ => "Неизвестно"
    };
}

public sealed record MaxSendResult(MaxDeliveryState State, string MessageId = "", string Error = "", TimeSpan? RetryAfter = null);
public sealed record MaxGroup(long Id, string Title)
{
    public override string ToString() => $"{Title} · {Id}";
}
