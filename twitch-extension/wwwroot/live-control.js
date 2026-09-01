const isLocalPreview=['localhost','127.0.0.1'].includes(location.hostname);
const relayBase=(isLocalPreview?location.origin:(window.ELDENINTEL_RELAY_BASE||'')).replace(/\/$/,'');
const api=path=>`${relayBase}${path}`;
let token='';
const $=id=>document.getElementById(id);

function headers(){const value={'Content-Type':'application/json'};if(token)value.Authorization=`Bearer ${token}`;return value}
function render(snapshot){
  const online=snapshot.consumerOnline===true;
  $('controlConnection').className=`control-light ${online?'online':'offline'}`;
  $('controlState').textContent=`${snapshot.armed===false?'PAUSED':'ARMED'} · ${online?'ELDENINTEL ONLINE':'ELDENINTEL OFFLINE'}`;
  $('controlQueue').textContent=`${snapshot.queue?.length||0} queued${snapshot.active?' · 1 active':''}`;
}
async function refresh(){try{const response=await fetch(api('/api/state'),{cache:'no-store',headers:headers()});if(response.ok)render(await response.json())}catch{$('controlState').textContent='RELAY OFFLINE'}}
async function control(action){
  $('controlMessage').textContent='WORKING…';
  try{const response=await fetch(api('/api/control'),{method:'POST',headers:headers(),body:JSON.stringify({action})});const body=await response.json();if(!response.ok){$('controlMessage').textContent=body.message||'CONTROL REJECTED';return}render(body);$('controlMessage').textContent=action==='clear'?'QUEUE CLEARED':action==='arm'?'INTERACTIONS ARMED':'INTERACTIONS PAUSED'}catch{$('controlMessage').textContent='ELDENINTEL RELAY OFFLINE'}
}
document.querySelector('.control-grid').addEventListener('click',event=>{const button=event.target.closest('[data-action]');if(button)control(button.dataset.action)});
if(window.Twitch?.ext)Twitch.ext.onAuthorized(auth=>{token=auth.token||'';refresh()});else refresh();
setInterval(refresh,3000);
