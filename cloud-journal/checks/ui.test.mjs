import test from 'node:test';
import assert from 'node:assert/strict';
import {duration,localDate,esc,shops,status,nextStage,parseJournal} from '../src/CraneJournal.Web/wwwroot/ui.mjs';
const id='5eccacdc-d77a-4cf0-85d1-5a232c2d911b';
const legacy={Version:5,Records:[{Id:id,ReportedAt:'2026-10-01T08:00:00',ShopNumber:'01',CraneNumber:'04А',ReporterName:'Иванов И.И.',FaultDescription:'Проверка',WorksWithRestrictions:true}],Attachments:[],MaxOutbox:[{MediaToken:'DO-NOT-IMPORT'}],Token:'DO-NOT-IMPORT'};
test('duration retains days and local timestamps do not shift by the phone timezone',()=>{
  assert.equal(duration(1575),'26 ч 15 мин');assert.equal(duration(0),'0 ч 00 мин');
  assert.equal(localDate('2026-10-01T08:20:00'),'01.10.2026 08:20');assert.equal(localDate(null),'—');
});
test('user text is escaped before HTML insertion',()=>assert.equal(esc('<img src=x onerror="x()">&\''),'&lt;img src=x onerror=&quot;x()&quot;&gt;&amp;&#39;'));
test('shops retain leading zeros and letters',()=>assert.deepEqual(shops('01, 4А;01\n02'),['01','4А','02']));
test('workflow offers only the next stage',()=>{
  assert.equal(nextStage({}),'Responded');assert.equal(nextStage({respondedAt:'x'}),'RepairStarted');
  assert.equal(nextStage({respondedAt:'x',repairStartedAt:'x'}),'Restored');assert.equal(nextStage({restoredAt:'x'}),null);
  assert.equal(status({respondedAt:'x'})[0],'Ожидает выдачи');
});
test('import preserves SDU and excludes settings, old outbox and arbitrary record fields',()=>{
  const doc=structuredClone(legacy);doc.Records[0].Untrusted='secret';const result=parseJournal(JSON.stringify(doc));
  assert.equal(result.records[0].worksWithRestrictions,true);assert.equal(result.records[0].shopNumber,'01');
  assert.ok(!JSON.stringify(result).includes('DO-NOT-IMPORT'));assert.ok(!JSON.stringify(result).includes('secret'));
});
test('import rejects duplicate IDs, missing records and unsafe attachment paths',()=>{
  assert.throws(()=>parseJournal('{}'));assert.throws(()=>parseJournal(JSON.stringify({...legacy,Records:[legacy.Records[0],legacy.Records[0]]})));
  assert.throws(()=>parseJournal(JSON.stringify({...legacy,Attachments:[{RecordId:id,OriginalFileName:'x.png',StoredFileName:'../max-settings.json',SizeBytes:10,Sha256:'a'.repeat(64)}]})));
});
test('old journals without attachments remain importable',()=>{
  const {Attachments,...old}=legacy;assert.deepEqual(parseJournal(JSON.stringify({...old,Version:1})).attachments,[]);
});

// Execute the actual client with small DOM/API doubles. These checks cover request
// behaviour, not browser layout and not the ASP.NET implementation.
test('lost response retains the same command; resubmitting cannot create a new one',async()=>{
  const listeners={}, windowListeners={}, nodes=new Map();
  const node=()=>({innerHTML:'',textContent:'',hidden:true,className:'',focus(){}});
  globalThis.document={querySelector(s){if(!nodes.has(s))nodes.set(s,node());return nodes.get(s);},addEventListener(k,fn){listeners[k]=fn;}};
  globalThis.window={addEventListener(k,fn){windowListeners[k]=fn;},scrollTo(){}};
  globalThis.location={hash:'#/new'};
  const storage={};Object.defineProperties(storage,{getItem:{value:k=>storage[k]??null},setItem:{value:(k,v)=>{storage[k]=String(v);}},removeItem:{value:k=>{delete storage[k];}}});globalThis.sessionStorage=storage;
  const OriginalFormData=globalThis.FormData;
  globalThis.FormData=class{constructor(f){this.data=f.values;}*[Symbol.iterator](){yield* Object.entries(this.data);}};
  const user={id:'9c9c49a1-5a5e-4121-85fb-4b485cd945ef',name:'Сотрудник',canWrite:true,admin:false,mustChangePassword:false,shops:['01']};
  let mode='offline';const requests=[];
  globalThis.fetch=async(path,init)=>{
    if(path==='/api/session')return Response.json({user,csrf:'test-csrf',nowLocal:'2026-10-01T08:00:00',timeZone:'Europe/Moscow',maxEnabled:false});
    if(path==='/api/shops')return Response.json(['01']);
    if(path==='/api/records'&&init.method==='POST'){
      requests.push(JSON.parse(init.body));assert.equal(init.headers['X-CSRF-TOKEN'],'test-csrf');
      if(mode==='offline')throw Error('connection lost');
      if(mode==='truncated')return new Response('{',{status:200});
      if(mode==='conflict')return Response.json({error:'Conflict'},{status:409});
      return Response.json({id:requests.at(-1).commandId,revision:1});
    }
    throw Error('Unexpected fixture request: '+path);
  };
  try{
    await import('../src/CraneJournal.Web/wwwroot/app.mjs');
    const form={dataset:{form:'new'},values:{shop:'01',crane:'04А',fault:'Неисправность',eventAt:''},querySelectorAll(){return [];},closest(){return this;}};
    const submit=()=>listeners.submit({target:form,preventDefault(){}});
    await submit();assert.equal(requests.length,1);const first=requests[0].commandId;
    assert.equal(Object.keys(storage).filter(k=>k.startsWith('crane-command:')).length,1);
    await submit();assert.equal(requests.length,1,'second submit must not create another command');
    const button={dataset:{action:'retry',id:first},disabled:false};const click=()=>listeners.click({target:{closest(selector){return selector==='[data-action]'?button:null;}}});
    mode='truncated';await click();assert.equal(requests.at(-1).commandId,first);assert.ok(storage['crane-command:'+first]);
    mode='ok';await click();assert.equal(requests.at(-1).commandId,first);assert.equal(storage['crane-command:'+first],undefined);
    assert.equal(location.hash,'/record/'+first);
    // Definitive rejection permits correction instead of retaining a doomed retry.
    mode='conflict';await submit();assert.equal(Object.keys(storage).filter(k=>k.startsWith('crane-command:')).length,0);
  }finally{globalThis.FormData=OriginalFormData;}
});
