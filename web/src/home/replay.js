/* An event-driven illustration, without a feed, timer or console dependency.
   The complete default snapshot is already present in index.html. */
const closes = [24988.0,24995.97,25000.75,25009.53,25009.16,25020.49,25017.66,25008.85,25010.29,25010.39,25005.27,25016.88,25021.08,25027.42,25037.64,25037.09,25031.07,25031.79,25023.35,25027.45,25028.19,25038.13,25045.94,25055.26,25066.69,25061.7,25061.28,25058.44,25052.15,25050.42,25050.25,25048.92,25054.13,25054.15,25054.09,25051.33,25041.02,25030.58,25026.7,25023.11,25024.45,25029.6,25033.81,25039.04,25038.36,25039.88,25037.83,25034.5,25031.4];
const rows = [
  {strike:24950, ce:134.2, pe:52.8, callOi:42.6, putOi:78.4, delta:.69},
  {strike:25000, ce:100.9, pe:69.5, callOi:61.8, putOi:104.2, delta:.57},
  {strike:25050, ce:73.2, pe:91.8, callOi:86.4, putOi:91.6, delta:.45},
  {strike:25100, ce:51.5, pe:120.1, callOi:112.6, putOi:48.8, delta:.33},
  {strike:25150, ce:34.9, pe:153.5, callOi:74.8, putOi:31.2, delta:.24}
];
const slider=document.querySelector('#minute-slider');
const chainRows=[...document.querySelectorAll('#preview-chain tr')];
const candles=[...document.querySelectorAll('.sample-candle')];
const modeButtons=[...document.querySelectorAll('[data-chain-mode]')];
const format=(value,decimals=2)=>value.toLocaleString('en-IN',{
  minimumFractionDigits:decimals,maximumFractionDigits:decimals
});
let mode='premiums';
function renderSnapshot() {
  const minute=Number(slider.value),minuteOfDay=585+minute;
  const time=`${String(Math.floor(minuteOfDay/60)).padStart(2,'0')}:${String(minuteOfDay%60).padStart(2,'0')} IST`;
  const position=minute/5,left=Math.floor(position);
  const spot=closes[left]+(closes[Math.min(left+1,48)]-closes[left])*(position-left);
  const atmStrike=Math.round(spot/50)*50,atmIndex=rows.findIndex(row=>row.strike===atmStrike);
  const move=spot-25031.4,x=10+position*10.9,y=121-(spot-25000)*1.2,age=(240-minute)/240;
  document.querySelector('#sample-spot').textContent=format(spot);
  document.querySelector('#quote-time').textContent=time;
  document.querySelector('#replay-time').textContent=time;
  slider.setAttribute('aria-valuetext',time);
  document.querySelector('#sample-cursor').setAttribute('transform',`translate(${x.toFixed(2)} 0)`);
  document.querySelector('#cursor-dot').setAttribute('cy',y.toFixed(2));
  document.querySelector('#sample-price-line').setAttribute('d',`M8 ${y.toFixed(2)}h534`);
  document.querySelector('#quote-tag').setAttribute('y',(y-9).toFixed(2));
  document.querySelector('#chart-price').setAttribute('y',(y+3.5).toFixed(2));
  document.querySelector('#chart-price').textContent=format(spot);
  // Keep nearby scale labels out of the price tag, including during replay.
  document.querySelectorAll('[data-price-axis]').forEach(label=>{
    label.setAttribute('visibility',Math.abs(Number(label.dataset.priceAxis)-(y+3.5))<20?'hidden':'visible');
  });
  document.querySelector('#sample-pcr').textContent=(rows.reduce((sum,r)=>sum+r.putOi*(1-age*.17),0)/rows.reduce((sum,r)=>sum+r.callOi*(1-age*.12),0)).toFixed(2);
  document.querySelector('#sample-iv').textContent=(12.42+age*.72).toFixed(2)+'%';
  candles.forEach((c,i)=>c.style.opacity=i<=position?'1':'.18');
  document.querySelector('#ce-outer-heading').textContent=mode==='premiums'?'CALL OI · L':'CALL Δ';
  document.querySelector('#ce-inner-heading').textContent=mode==='premiums'?'LTP · ₹':'CALL IV';
  document.querySelector('#pe-inner-heading').textContent=mode==='premiums'?'LTP · ₹':'PUT IV';
  document.querySelector('#pe-outer-heading').textContent=mode==='premiums'?'PUT OI · L':'PUT Δ';
  chainRows.forEach((tr,i)=>{
    const r=rows[i],cells=tr.querySelectorAll('td');
    // Preserve CE - PE = spot - strike as the selected minute changes.
    const ce=Math.max(.05,r.ce+move*r.delta+age*4.4),pe=ce-(spot-r.strike);
    const callOi=r.callOi*(1-age*.12),putOi=r.putOi*(1-age*.17);
    const delta=Math.max(.04,Math.min(.96,r.delta+move*.003)),iv=12.42+Math.abs(i-atmIndex)*.19+age*.72;
    if(mode==='premiums'){
      cells[0].innerHTML=`<i class="oi-bar" style="--bar:${Math.min(86,callOi/120*86).toFixed(1)}%"></i><span class="oi-value">${format(callOi,1)}</span>`;
      cells[1].textContent=format(ce);
      cells[2].textContent=format(Math.max(.05,pe));
      cells[3].innerHTML=`<i class="oi-bar put" style="--bar:${Math.min(86,putOi/120*86).toFixed(1)}%"></i><span class="oi-value">${format(putOi,1)}</span>`;
    }else{
      cells[0].textContent='+'+delta.toFixed(2);
      cells[1].textContent=iv.toFixed(2)+'%';
      cells[2].textContent=iv.toFixed(2)+'%';
      cells[3].textContent='−'+(1-delta).toFixed(2);
    }
    tr.classList.toggle('atm',r.strike===atmStrike);
  });
}
slider.addEventListener('input',renderSnapshot);
modeButtons.forEach(button=>button.addEventListener('click',()=>{
  mode=button.dataset.chainMode;
  modeButtons.forEach(item=>item.setAttribute('aria-pressed',String(item===button)));
  renderSnapshot();
}));
