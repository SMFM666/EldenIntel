const extensionClientId='uokh78i6q53c9ozc7fwex439qdx3li';
const effects=[
  {id:'spawn-clone',name:'SPAWN CLONE',cooldown:180,enabled:true},
  {id:'summon-boss',name:'SUMMON A BOSS',cooldown:300,enabled:true},
  {id:'life-steal',name:'LIFE STEAL',cooldown:60,enabled:true},
  {id:'heal-player',name:'HEAL PLAYER',cooldown:60,enabled:true},
  {id:'slow-world',name:'SLOW WORLD',cooldown:120,enabled:true},
  {id:'random',name:'RANDOM EFFECT',cooldown:300,enabled:true}
];
const isLocalPreview=['localhost','127.0.0.1'].includes(location.hostname);
const relayBase=(isLocalPreview?location.origin:(window.ELDENINTEL_RELAY_BASE||'')).replace(/\/$/,'');
const api=path=>`${relayBase}${path}`;
const state={armed:true,consumerOnline:false,queue:[],active:null,viewer:'LOCAL VIEWER',capacity:8,token:'',cooldowns:{}};
let relaySocket=null,reconnectTimer=null;
let lastCompletedEffectId='';
let bossCatalog=[];
const $=id=>document.getElementById(id);
function init(){
  const fireField='<span class="boss-fire-field" aria-hidden="true">'+Array.from({length:10},(_,i)=>{const rise=34+(i*19)%34,drift=((i*17)%23)-11;return `<i style="--left:${5+i*10}%;--delay:-${((i*37)%97)/100}s;--rise:-${rise}px;--mid-rise:-${Math.round(rise*.52)}px;--drift:${drift}px;--mid-drift:${Math.round(drift*.45)}px"></i>`}).join('')+'</span>';
  const buttonFireField=fireField.replace('aria-hidden="true">','aria-hidden="true"><b class="boss-fire-emitter"></b>');
  $('effectGrid').innerHTML=effects.map(effect=>`<button class="effect effect-${effect.id}" data-id="${effect.id}" ${effect.enabled?'':'disabled'}>${effect.id==='random'?`<span class="random-plate random-plate-dark" aria-hidden="true"></span><span class="random-plate random-plate-silver" aria-hidden="true"></span><span class="random-face" aria-hidden="true"></span>`:effect.id==='life-steal'?`<span class="life-bubble-field" aria-hidden="true"><span class="life-circle life-top"></span><span class="life-circle life-top"></span><span class="life-circle life-top"></span><span class="life-bubble-core"></span><span class="life-circle life-bottom"></span><span class="life-circle life-bottom"></span><span class="life-circle life-bottom"></span></span>`:effect.id==='heal-player'?`<span class="heal-orb" aria-hidden="true"><svg viewBox="0 0 24 24" focusable="false"><path d="M9.4 3h5.2v6.4H21v5.2h-6.4V21H9.4v-6.4H3V9.4h6.4z"/></svg></span>`:effect.id==='slow-world'?`<span class="slow-visual" aria-hidden="true"><span class="slow-grid"></span><span class="slow-trail">${Array.from({length:12},(_,i)=>`<i style="--trail-opacity:${Math.max(.08,.34-i*.022).toFixed(3)}"></i>`).join('')}</span><span class="slow-echoes"><b>SLOW WORLD</b><b>SLOW WORLD</b><b>SLOW WORLD</b></span></span>`:effect.id==='summon-boss'?`<span class="boss-button-fire" aria-hidden="true"><span class="boss-fire-surface"></span>${buttonFireField}</span>`:''}<strong>${effect.name}</strong><span class="cooldown-badge unavailable" aria-live="polite" aria-label="Unavailable"></span></button>`).join('');
  $('bossSearchFire').insertAdjacentHTML('afterbegin',fireField);
  initSlowWorldMotion();
  initLauncherMotion();
  initPanelMotion();
  $('effectGrid').addEventListener('click',event=>{const button=event.target.closest('.effect');if(!button)return;if(button.dataset.id==='summon-boss')openBossPicker();else if(button.dataset.id==='spawn-clone')startCloneVote();else enqueue(button.dataset.id)});
  $('cloneHelp').addEventListener('click',()=>castCloneVote('help'));
  $('cloneHurt').addEventListener('click',()=>castCloneVote('hurt'));
  $('bossPickerClose').addEventListener('click',closeBossPicker);
  $('bossSearch').addEventListener('input',renderBossResults);
  $('bossSearch').addEventListener('keydown',event=>{if(event.key==='Escape'){event.stopPropagation();closeBossPicker()}});
  $('bossResults').addEventListener('click',event=>{const choice=event.target.closest('[data-boss]');if(!choice)return;const name=choice.dataset.boss;closeBossPicker();enqueue('summon-boss',name)});
  $('launcher').addEventListener('click',()=>setPanelOpen($('overlay').dataset.open!=='true'));
  const smashButton=$('smashButton');
  let lastSmashTouchAt=0;
  smashButton.addEventListener('touchend',event=>{
    event.preventDefault();event.stopPropagation();lastSmashTouchAt=Date.now();registerSmash();
  },{passive:false});
  smashButton.addEventListener('click',event=>{
    event.preventDefault();event.stopPropagation();
    if(Date.now()-lastSmashTouchAt>700)registerSmash();
  });
  smashButton.addEventListener('dblclick',event=>{event.preventDefault();event.stopPropagation()});
  ['pointerdown','pointerup','touchstart'].forEach(type=>smashButton.addEventListener(type,event=>event.stopPropagation(),{passive:false}));
  const overlay=$('overlay');
  ['click','dblclick','mousedown','mouseup','pointerdown','pointerup','touchstart','touchend'].forEach(type=>overlay.addEventListener(type,event=>{
    if(!overlay.classList.contains('smash-input-shield'))return;
    const onSmashButton=Boolean(event.target.closest?.('#smashButton'));
    if(type==='dblclick'){event.preventDefault();event.stopPropagation();return}
    if(onSmashButton)return;
    event.preventDefault();event.stopPropagation();
  },{capture:true,passive:false}));
  if(isLocalPreview)$('queuePanel').addEventListener('click',()=>startSmashEvent(20,40));
  document.addEventListener('keydown',event=>{if(event.key==='Escape')setPanelOpen(false)});
  if(window.Twitch?.ext){Twitch.ext.onAuthorized(auth=>{state.token=auth.token||'';state.viewer=auth.userId?.startsWith('U-')?'TWITCH VIEWER':'ANONYMOUS';$('viewerLabel').textContent=state.viewer;connectRelaySocket();console.info('Elden Intel extension authorized',extensionClientId,auth.channelId)})}
  else connectRelaySocket();
  render(); refreshState(); setInterval(render,250);setInterval(()=>{if(!relaySocket||relaySocket.readyState!==WebSocket.OPEN)refreshState()},5000);
}
let smashState={active:false,count:0,target:40,endsAt:0,timer:0,startDamage:0,startHits:0};
let lastSmashEventId='';
let smashEntranceFrame=0,smashEntranceToken=0,smashExitFrame=0,smashExitTimer=0;
let smashTotalFrame=0,smashTotalToken=0;
function startSmashEvent(seconds=15,target=40){
  clearTimeout(smashExitTimer);cancelAnimationFrame(smashExitFrame);$('overlay').classList.add('smash-input-shield');
  clearInterval(smashState.timer);smashState={active:true,count:0,target,endsAt:Date.now()+seconds*1000,timer:0,startDamage:state.hurtDamage||0,startHits:state.hurtHits||0};
  const total=$('smashTotal');smashTotalToken++;cancelAnimationFrame(smashTotalFrame);total.setAttribute('aria-hidden','true');total.innerHTML='';total.style.cssText='';
  const event=$('smashEvent');event.classList.remove('active','staged','complete','failed');event.setAttribute('aria-hidden','false');
  cancelAnimationFrame(smashEntranceFrame);const token=++smashEntranceToken;
  event.style.transition='none';event.style.visibility='visible';event.style.pointerEvents='none';event.style.opacity='0';event.style.transform='translate(-50%, 250px) scale(.84)';
  updateSmashEvent();
  setTimeout(()=>{
    if(token!==smashEntranceToken)return;const started=performance.now(),duration=1550;
    const tick=now=>{
      if(token!==smashEntranceToken)return;const raw=Math.min(1,(now-started)/duration);
      const eased=1-Math.pow(1-raw,4),overshoot=Math.sin(raw*Math.PI)*8*(1-raw);
      const y=250*(1-eased)-overshoot,scale=.84+.16*eased;
      event.style.transform=`translate(-50%, ${y.toFixed(2)}px) scale(${scale.toFixed(4)})`;
      event.style.opacity=String(Math.min(1,raw*3.2));
      if(raw<1)smashEntranceFrame=requestAnimationFrame(tick);else{event.style.transform='translate(-50%, 0) scale(1)';event.style.opacity='1';event.style.pointerEvents='auto';event.classList.add('active')}
    };smashEntranceFrame=requestAnimationFrame(tick);
  },350);
  smashState.timer=setInterval(updateSmashEvent,50);
}
function registerSmash(){
  if(!smashState.active)return;smashState.count++;enqueue('hurt-seth',null,true);$('smashButton').classList.remove('impact');void $('smashButton').offsetWidth;$('smashButton').classList.add('impact');
  setTimeout(()=>$('smashButton').classList.remove('impact'),560);
  updateSmashEvent();
}
function updateSmashEvent(){
  if(!smashState.active)return;const remaining=Math.max(0,smashState.endsAt-Date.now());
  $('smashCount').textContent=smashState.count;$('smashTimer').textContent=(remaining/1000).toFixed(1);$('smashFill').style.width=`${Math.min(100,smashState.count/smashState.target*100)}%`;
  $('smashEvent').style.setProperty('--smash-energy',String(Math.min(1,smashState.count/smashState.target)));
  if(remaining<=0)finishSmashEvent(false);
}
function finishSmashEvent(success){
  if(!smashState.active)return;smashState.active=false;clearInterval(smashState.timer);$('smashEvent').classList.add(success?'complete':'failed');
  $('smashButton').querySelector('.smash-title').innerHTML=`<strong>${smashState.count}</strong><b>HITS LANDED</b>`;
  clearTimeout(smashExitTimer);smashExitTimer=setTimeout(()=>{
    const event=$('smashEvent'),token=++smashEntranceToken;cancelAnimationFrame(smashEntranceFrame);cancelAnimationFrame(smashExitFrame);
    event.style.transition='none';event.style.pointerEvents='none';const started=performance.now(),duration=700;
    const tick=now=>{
      if(token!==smashEntranceToken)return;const raw=Math.min(1,(now-started)/duration),eased=raw*raw*(3-2*raw);
      event.style.transform=`translate(-50%, ${(190*eased).toFixed(2)}px) scale(${(1-.12*eased).toFixed(4)})`;
      event.style.opacity=String(1-eased);
      if(raw<1)smashExitFrame=requestAnimationFrame(tick);else{
        event.classList.remove('active','staged','complete','failed');event.setAttribute('aria-hidden','true');event.style.cssText='';$('overlay').classList.remove('smash-input-shield');$('smashButton').classList.remove('impact');$('smashButton').querySelector('.smash-title').innerHTML='<strong>HURT SETH</strong><b>BUTTON</b>';showSmashTotal();
      }
    };smashExitFrame=requestAnimationFrame(tick);
  },650);
}
function showSmashTotal(){const damage=Math.max(0,(state.hurtDamage||0)-smashState.startDamage),hits=Math.max(0,(state.hurtHits||0)-smashState.startHits),maximum=state.hurtMaxHp||0,percent=maximum>0?damage/maximum*100:0,total=$('smashTotal'),token=++smashTotalToken;cancelAnimationFrame(smashTotalFrame);total.innerHTML=`<strong>${damage.toLocaleString()} DAMAGE DEALT</strong><span>${hits} HIT${hits===1?'':'S'} · ${percent.toFixed(1)}% MAX HP</span>`;total.setAttribute('aria-hidden','false');total.style.visibility='visible';total.style.opacity='0';total.style.filter='blur(8px)';total.style.transform='translate(-50%,70px) scale(.8)';const started=performance.now(),duration=4200;const tick=now=>{if(token!==smashTotalToken)return;const p=Math.min(1,(now-started)/duration),enter=Math.min(1,p/.2),leave=Math.max(0,(p-.72)/.28),rise=1-Math.pow(1-enter,4),fade=leave*leave*(3-2*leave),y=70*(1-rise)-25*fade,scale=.8+.23*rise-.05*fade;total.style.opacity=String(Math.min(1,enter*2)*(1-fade));total.style.filter=`blur(${(8*(1-rise)+5*fade).toFixed(2)}px)`;total.style.transform=`translate(-50%,${y.toFixed(2)}px) scale(${scale.toFixed(4)})`;if(p<1)smashTotalFrame=requestAnimationFrame(tick);else{total.style.visibility='hidden';total.setAttribute('aria-hidden','true')}};smashTotalFrame=requestAnimationFrame(tick)}
function initLauncherMotion(){
  const launcher=$('launcher');if(!launcher)return;
  launcher.addEventListener('pointermove',event=>{if(event.pointerType==='touch')return;const r=launcher.getBoundingClientRect(),nx=(event.clientX-r.left)/r.width-.5,ny=(event.clientY-r.top)/r.height-.5;launcher.style.setProperty('--launcher-ry',`${nx*24}deg`);launcher.style.setProperty('--launcher-rx',`${ny*-24}deg`);launcher.style.setProperty('--launcher-light-x',`${(nx+.5)*100}%`);launcher.style.setProperty('--launcher-light-y',`${(ny+.5)*100}%`)});
  launcher.addEventListener('pointerleave',()=>{launcher.style.setProperty('--launcher-ry','0deg');launcher.style.setProperty('--launcher-rx','0deg');launcher.style.setProperty('--launcher-light-x','50%');launcher.style.setProperty('--launcher-light-y','50%')});
}
function initPanelMotion(){
  ['effectsPanel','queuePanel'].forEach(id=>{const panel=$(id);if(!panel)return;panel.addEventListener('pointermove',event=>{if(event.pointerType==='touch')return;const r=panel.getBoundingClientRect(),nx=Math.max(-.5,Math.min(.5,(event.clientX-r.left)/r.width-.5)),ny=Math.max(-.5,Math.min(.5,(event.clientY-r.top)/r.height-.5));panel.style.setProperty('--panel-ry',`${nx*8}deg`);panel.style.setProperty('--panel-rx',`${ny*-8}deg`);panel.style.setProperty('--panel-light-x',`${(nx+.5)*100}%`);panel.style.setProperty('--panel-light-y',`${(ny+.5)*100}%`)});panel.addEventListener('pointerleave',()=>{panel.style.setProperty('--panel-ry','0deg');panel.style.setProperty('--panel-rx','0deg');panel.style.setProperty('--panel-light-x','50%');panel.style.setProperty('--panel-light-y','50%')})});
}
function initSlowWorldMotion(){
  const button=document.querySelector('.effect-slow-world');if(!button)return;
  const motes=[...button.querySelectorAll('.slow-trail i')],points=motes.map(()=>({x:0,y:0}));
  let target={x:0,y:0},last={x:0,y:0,t:performance.now()},frame=0,active=false;
  const center=()=>{const r=button.getBoundingClientRect();target={x:r.width/2,y:r.height/2};points.forEach(p=>{p.x=target.x;p.y=target.y})};center();
  function tick(){if(!active)return;let lead=target;points.forEach((p,index)=>{const ease=.24-index*.008;p.x+=(lead.x-p.x)*ease;p.y+=(lead.y-p.y)*ease;motes[index].style.transform=`translate(${p.x-6}px,${p.y-6}px) scale(${1-index*.045})`;lead=p});frame=requestAnimationFrame(tick)}
  button.addEventListener('pointerenter',()=>{active=true;center();cancelAnimationFrame(frame);frame=requestAnimationFrame(tick)});
  button.addEventListener('pointermove',event=>{const r=button.getBoundingClientRect(),now=performance.now(),x=event.clientX-r.left,y=event.clientY-r.top,dt=Math.max(8,now-last.t),speed=Math.min(1,Math.hypot(x-last.x,y-last.y)/dt*.42),echo=2+speed*8;target={x,y};button.style.setProperty('--slow-shift-x',`${Math.max(-12,Math.min(12,(x-r.width/2)*.13))}px`);button.style.setProperty('--slow-shift-y',`${Math.max(-7,Math.min(7,(y-r.height/2)*.13))}px`);button.style.setProperty('--slow-blur',`${.5+speed*4}px`);button.style.setProperty('--slow-echo',`${echo}px`);button.style.setProperty('--slow-echo-neg',`${-echo}px`);button.style.setProperty('--slow-echo-y',`${echo*.45}px`);last={x,y,t:now}});
  button.addEventListener('pointerleave',()=>{active=false;cancelAnimationFrame(frame);button.style.setProperty('--slow-blur','.5px');button.style.setProperty('--slow-echo','2px');button.style.setProperty('--slow-echo-neg','-2px');button.style.setProperty('--slow-echo-y','.9px')});
}
function setPanelOpen(open){$('overlay').dataset.open=String(open);$('launcher').setAttribute('aria-expanded',String(open));$('effectsPanel').setAttribute('aria-hidden',String(!open));const hint=$('launcher').querySelector('.launcher-copy em');if(hint)hint.textContent=open?'CLOSE EFFECTS':'OPEN EFFECTS'}
async function openBossPicker(){
  if(!state.armed||!state.consumerOnline||cooldownRemaining('summon-boss')>0)return;
  $('bossPicker').hidden=false;$('bossSearch').value='';$('bossSearchHint').textContent='Loading verified bosses…';
  if(!bossCatalog.length)await loadBossCatalog();
  renderBossResults();$('bossSearch').focus();
}
function closeBossPicker(){$('bossPicker').hidden=true;$('bossResults').innerHTML=''}
async function loadBossCatalog(){
  try{const headers={};if(state.token)headers.Authorization=`Bearer ${state.token}`;const response=await fetch(api('/api/bosses'),{cache:'no-store',headers});if(!response.ok)throw new Error();bossCatalog=await response.json();$('bossSearchHint').textContent='Choose one verified result to summon.'}
  catch{$('bossSearchHint').textContent='Boss catalog is temporarily unavailable.'}
}
function renderBossResults(){
  const query=$('bossSearch').value.trim().toLocaleLowerCase();
  if(query.length<3){$('bossResults').innerHTML='';$('bossSearchHint').textContent=query.length?`Type ${3-query.length} more character${3-query.length===1?'':'s'} to search.`:'Type at least 3 characters to search.';return}
  const matches=bossCatalog.filter(name=>name.toLocaleLowerCase().includes(query)).slice(0,6);
  $('bossResults').innerHTML=matches.map(name=>`<button type="button" role="option" data-boss="${escapeHtml(name)}">${escapeHtml(name)}</button>`).join('');
  if(bossCatalog.length)$('bossSearchHint').textContent=matches.length?`${matches.length} matching boss${matches.length===1?'':'es'}`:'No verified boss matches that name.';
}

function connectRelaySocket(){
  clearTimeout(reconnectTimer);if(relaySocket&&relaySocket.readyState<2)relaySocket.close();
  const root=relayBase||location.origin;const wsRoot=root.replace(/^https:/,'wss:').replace(/^http:/,'ws:');
  try{
    relaySocket=new WebSocket(`${wsRoot}/ws/viewer`);
    relaySocket.addEventListener('open',()=>{relaySocket.send(JSON.stringify({token:state.token||''}))});
    relaySocket.addEventListener('message',event=>{try{applySnapshot(JSON.parse(event.data))}catch{}});
    relaySocket.addEventListener('close',()=>{reconnectTimer=setTimeout(connectRelaySocket,1500)});
    relaySocket.addEventListener('error',()=>relaySocket.close());
  }catch{reconnectTimer=setTimeout(connectRelaySocket,1500)}
}
async function enqueue(id,bossName=null,quiet=false){
  if(!state.armed)return toast('INTERACTIONS ARE DISARMED');
  try{
    const headers={'Content-Type':'application/json'};if(state.token)headers.Authorization=`Bearer ${state.token}`;
    const response=await fetch(api('/api/effects'),{method:'POST',headers,body:JSON.stringify({effectId:id,viewer:state.viewer,bossName})});
    const body=await response.json();
    if(!response.ok){if(!quiet)toast(body.message||'EFFECT REJECTED');return}
    applySnapshot(body);if(!quiet)toast(`${effectName(id)} ADDED`);
  }catch{if(!quiet)return toast('ELDENINTEL RELAY OFFLINE')}
}
async function startCloneVote(){
  try{const headers={'Content-Type':'application/json'};if(state.token)headers.Authorization=`Bearer ${state.token}`;const response=await fetch(api('/api/clone-vote/start'),{method:'POST',headers,body:JSON.stringify({viewer:state.viewer})});const body=await response.json();if(!response.ok)return toast(body.message||'VOTE REJECTED');applySnapshot(body);toast('CLONE VOTE STARTED')}
  catch{toast('ELDENINTEL RELAY OFFLINE')}
}
async function castCloneVote(choice){
  if(state.cloneVoteChoice)return toast('YOU ALREADY VOTED');
  try{const headers={'Content-Type':'application/json'};if(state.token)headers.Authorization=`Bearer ${state.token}`;const response=await fetch(api('/api/clone-vote/vote'),{method:'POST',headers,body:JSON.stringify({choice,viewer:state.viewer})});const body=await response.json();if(!response.ok)return toast(body.message||'VOTE REJECTED');state.cloneVoteChoice=choice;applySnapshot(body);toast(`VOTED ${choice.toUpperCase()}`)}
  catch{toast('ELDENINTEL RELAY OFFLINE')}
}
async function refreshState(){
  try{const headers={};if(state.token)headers.Authorization=`Bearer ${state.token}`;const response=await fetch(api('/api/state'),{cache:'no-store',headers});if(response.ok)applySnapshot(await response.json())}catch{}
}
function applySnapshot(snapshot){state.capacity=snapshot.capacity||8;state.armed=snapshot.armed!==false;state.consumerOnline=snapshot.consumerOnline===true;state.active=snapshot.active||null;state.queue=snapshot.queue||[];state.cooldowns=snapshot.cooldowns||{};state.hurtDamage=snapshot.hurtDamage||0;state.hurtHits=snapshot.hurtHits||0;state.hurtMaxHp=snapshot.hurtMaxHp||0;const priorVote=state.cloneVote;state.cloneVote=snapshot.cloneVote||null;if(state.cloneVote?.id!==priorVote?.id)state.cloneVoteChoice='';const completed=snapshot.lastCompleted;if(completed?.id&&completed.id!==lastCompletedEffectId){lastCompletedEffectId=completed.id;if(completed.effectId?.startsWith('spawn-clone-'))toast(completed.succeeded?`${completed.name} SPAWNED`:(completed.message||'CLONE SPAWN FAILED'))}const smash=snapshot.smashEvent;if(smash?.id&&smash.id!==lastSmashEventId){lastSmashEventId=smash.id;const seconds=Math.max(1,(Date.parse(smash.endsAt)-Date.now())/1000);startSmashEvent(seconds,smash.target||40)}render()}
function render(){
  $('armedBadge').textContent=state.armed?'ARMED':'PAUSED';$('armedBadge').className=`badge ${state.armed?'armed':'disarmed'}`;
  $('relayStatus').textContent=state.consumerOnline?'ELDENINTEL ONLINE':'ELDENINTEL OFFLINE';$('relayStatus').className=state.consumerOnline?'relay-online':'relay-offline';
  document.querySelectorAll('.effect').forEach(button=>{const effect=effects.find(x=>x.id===button.dataset.id);const remaining=cooldownRemaining(button.dataset.id);const available=state.armed&&state.consumerOnline&&effect?.enabled;const ready=available&&remaining===0;button.disabled=!ready;button.classList.toggle('cooling',remaining>0);const badge=button.querySelector('.cooldown-badge');if(badge){const status=remaining>0?'cooling':ready?'ready':'unavailable';badge.className=`cooldown-badge ${status}`;badge.textContent=remaining>0?`${remaining}s`:'';badge.setAttribute('aria-label',remaining>0?`Ready in ${remaining} seconds`:ready?'Ready':'Unavailable');badge.title=remaining>0?`Ready in ${remaining} seconds`:ready?'Ready':'Unavailable'}});
  $('queueCount').textContent=`${state.queue.length} / ${state.capacity}`;
  const active=$('activeEffect');
  if(state.active){const elapsed=Math.max(0,(Date.now()-Date.parse(state.active.startedAt))/1000);const progress=Math.min(1,elapsed/state.active.duration);active.className='active-effect';active.innerHTML=`<span class="active-index">LIVE</span><div><strong>${escapeHtml(state.active.bossName||state.active.name)}</strong><small>${escapeHtml(state.active.viewer)} · EXECUTING IN ELDENINTEL</small></div><div class="progress"><i style="width:${progress*100}%"></i></div>`}
  else{active.className='active-effect empty';active.innerHTML='<strong class="queue-idle">QUEUE <span class="queue-dots" aria-hidden="true"><i>.</i><i>.</i><i>.</i></span></strong>'}
  $('queueList').innerHTML=state.queue.map((item,index)=>`<li class="queue-item"><span class="queue-number">${String(index+1).padStart(2,'0')}</span><div><strong>${escapeHtml(item.bossName||item.name)}</strong><small>${escapeHtml(item.viewer)} · QUEUED</small></div><span class="eta">~${estimate(index)}s</span></li>`).join('');
  const vote=$('cloneVote');if(state.cloneVote){const remaining=Math.max(0,Date.parse(state.cloneVote.endsAt)-Date.now());vote.setAttribute('aria-hidden','false');vote.classList.add('active');$('cloneHelpVotes').textContent=state.cloneVote.helpVotes||0;$('cloneHurtVotes').textContent=state.cloneVote.hurtVotes||0;$('cloneVoteSeconds').textContent=(remaining/1000).toFixed(1);$('cloneVoteFill').style.width=`${Math.min(100,remaining/20000*100)}%`;$('cloneHelp').classList.toggle('selected',state.cloneVoteChoice==='help');$('cloneHurt').classList.toggle('selected',state.cloneVoteChoice==='hurt')}else{vote.setAttribute('aria-hidden','true');vote.classList.remove('active')}
}
function estimate(index){return Math.round((state.active?Math.max(0,state.active.duration-(Date.now()-Date.parse(state.active.startedAt))/1000):0)+state.queue.slice(0,index).reduce((sum,item)=>sum+item.duration,0))}
function cooldownRemaining(id){const readyAt=state.cooldowns?.[id];return readyAt?Math.max(0,Math.ceil((Date.parse(readyAt)-Date.now())/1000)):0}
function effectName(id){return effects.find(item=>item.id===id)?.name||'EFFECT'}
function escapeHtml(value){return String(value??'').replace(/[&<>"']/g,char=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[char]))}
let toastTimer;function toast(text){$('toast').textContent=text;$('toast').classList.add('show');clearTimeout(toastTimer);toastTimer=setTimeout(()=>$('toast').classList.remove('show'),1800)}
init();
