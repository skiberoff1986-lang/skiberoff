using System.Globalization;
using System.Text;

namespace EquipmentDowntime.Core;

public static class CsvExporter
{
    public static IReadOnlyList<string> Headers { get; } = Array.AsReadOnly(new[]
    {
        "Дата", "№ цеха", "№ крана", "Время", "Фамилия И.О.", "Описание неисправности",
        "Время реагирования", "Фамилия ответственного исполнителя", "Мастер смены",
        "Время выдачи крана в ремонт", "Время устранения неисправности",
        "Описание выполненных работ", "Время простоя", "Работа с СДУ с ограничениями", "Фото/видео"
    });

    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("ru-RU");
    public static string FormatEvent(DateTime? date) =>
        date?.ToString("dd.MM.yyyy HH:mm", Culture) ?? "";

    public static string Create(IEnumerable<DowntimeRecord> records, DateTime now, IEnumerable<JournalAttachment>? attachments = null)
    {
        var media = (attachments ?? []).ToLookup(a => a.RecordId);
        var text = new StringBuilder();
        AddRow(text, Headers);
        foreach (var r in records)
        {
            AddRow(text,
            [
                r.ReportedAt.ToString("dd.MM.yyyy", Culture),
                r.ShopNumber, r.CraneNumber,
                r.ReportedAt.ToString("HH:mm", Culture),
                r.ReporterName, r.FaultDescription, FormatEvent(r.RespondedAt),
                r.ResponsiblePerson, r.ShiftMaster, FormatEvent(r.RepairStartedAt),
                FormatEvent(r.RestoredAt), r.WorkDescription,
                (r.RestoredAt.HasValue ? "" : "Идёт: ") + TimeText.DurationWords(r.DowntimeAt(now)) + (r.WorksWithRestrictions ? " (не учтён)" : ""),
                r.WorksWithRestrictions ? "Да — исключён из суммы" : "Нет",
                string.Join(" | ", media[r.Id].Select(a => a.OriginalFileName))
            ]);
        }
        return text.ToString();
    }

    public static void Write(string path, IEnumerable<DowntimeRecord> records, DateTime now, IEnumerable<JournalAttachment>? attachments = null)
    {
        // UTF-8 BOM нужен для корректного открытия кириллицы в Excel.
        // Сначала полная временная выгрузка: прежний отчёт не теряется при сбое записи.
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, Create(records, now, attachments), new UTF8Encoding(true));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void AddRow(StringBuilder text, IEnumerable<string> values) =>
        text.AppendJoin(';', values.Select(Escape)).Append("\r\n");

    private static string Escape(string value)
    {
        // Пользовательский текст не должен исполняться как формула Excel.
        string trimmed = value.TrimStart();
        if (trimmed.Length > 0 && "=+-@".Contains(trimmed[0])) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
