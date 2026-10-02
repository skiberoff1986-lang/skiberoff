export const stages = {Registered:'Регистрация', Responded:'Реагирование', RepairStarted:'Выдача в ремонт', Restored:'Выдача из ремонта', AccountingChanged:'Учёт простоя / СДУ', Media:'Фото / видео', MediaAdded:'Добавлен файл', Imported:'Перенос из Windows', MaxQueued:'Вложение поставлено в очередь MAX', MaxRetry:'Повтор MAX', MaxCancelled:'Отправка MAX отменена'};
export const deliveries = {Pending:'В очереди', Sending:'Отправляется', Uploading:'Загрузка в MAX', Sent:'Отправлено', Blocked:'Нужна настройка', Uncertain:'Нужно проверить группу', Cancelled:'Отменено'};
export const esc = value => String(value ?? '').replace(/[&<>"']/g, x => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[x]));
export function duration(minutes) { const m = Math.max(0, Math.floor(Number(minutes)||0)); return `${Math.floor(m/60)} ч ${String(m%60).padStart(2,'0')} мин`; }
export function localDate(value) { if(!value)return '—'; const m=/^(\d{4})-(\d\d)-(\d\d)T(\d\d):(\d\d)/.exec(value); return m?`${m[3]}.${m[2]}.${m[1]} ${m[4]}:${m[5]}`:'—'; }
export function status(r) { return r.restoredAt ? ['Ремонт завершён','done'] : r.repairStartedAt ? ['В ремонте','active'] : r.respondedAt ? ['Ожидает выдачи','active'] : ['Новая заявка','active']; }
export function nextStage(r) { return r.restoredAt?null:!r.respondedAt?'Responded':!r.repairStartedAt?'RepairStarted':'Restored'; }
export function fileSize(bytes) { return `${(bytes/1e6).toLocaleString('ru-RU',{maximumFractionDigits:1})} МБ`; }
export function shops(text) { return [...new Set(text.split(/[;,\n]/).map(x=>x.trim()).filter(Boolean))]; }
export const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const fields = ['Id','ReportedAt','ShopNumber','CraneNumber','ReporterName','FaultDescription','RespondedAt','ResponsiblePerson','ShiftMaster','RepairStartedAt','RestoredAt','WorkDescription','WorksWithRestrictions'];
const get = (o,k) => o?.[k] ?? o?.[k[0].toLowerCase()+k.slice(1)];
// Only these fields can leave the browser. MAX settings, old queue and tokens are never imported.
export function parseJournal(text) {
  const doc=JSON.parse(text), list=get(doc,'Records');
  if(!Array.isArray(list)||!list.length||list.length>3000) throw Error('Выберите journal.json с 1–3000 заявками.');
  const records=list.map(r=>Object.fromEntries(fields.filter(k=>get(r,k)!==undefined).map(k=>[k[0].toLowerCase()+k.slice(1),get(r,k)])));
  const ids=new Set(); for(const r of records){if(!guid.test(r.id)||ids.has(r.id.toLowerCase()))throw Error('Неверный или повторный ID заявки.');r.id=r.id.toLowerCase();ids.add(r.id);}
  const attachments=(get(doc,'Attachments')??[]).map(a=>({recordId:String(get(a,'RecordId')).toLowerCase(), name:get(a,'OriginalFileName'), storedName:get(a,'StoredFileName'), size:get(a,'SizeBytes'), sha256:get(a,'Sha256')}));
  for(const a of attachments) if(!ids.has(a.recordId)||!/^([0-9a-f]{32})\.[a-z0-9]{2,5}$/i.test(a.storedName)||!Number.isSafeInteger(a.size)||a.size<=0||!/^[0-9a-f]{64}$/i.test(a.sha256)||typeof a.name!=='string')throw Error('Повреждены сведения о вложениях.');
  return {records, attachments};
}
