// NSE cash schedule only; exchange holidays are deliberately not inferred.
// All date parts are calculated in IST, independent of the visitor's timezone.
const formatter = new Intl.DateTimeFormat('en-GB', {
  timeZone: 'Asia/Kolkata',
  weekday: 'short',
  hour: '2-digit',
  minute: '2-digit',
  hourCycle: 'h23'
});
const weekdays = new Set(['Mon', 'Tue', 'Wed', 'Thu', 'Fri']);

export function formatMarketClock(date = new Date()) {
  const parts = Object.fromEntries(formatter.formatToParts(date).map(part => [part.type, part.value]));
  const minutes = Number(parts.hour) * 60 + Number(parts.minute);
  const open = weekdays.has(parts.weekday) && minutes >= 9 * 60 + 15 && minutes < 15 * 60 + 30;
  const time = `${parts.hour}:${parts.minute}`;
  return { open, time, weekday: parts.weekday, text: `NSE ${open ? 'open' : 'closed'} · ${time} IST` };
}

export function updateMarketClock(root = document, date = new Date()) {
  const clock = formatMarketClock(date);
  root.querySelectorAll('[data-market-clock]').forEach(node => {
    node.textContent = clock.text;
    node.title = 'NSE cash timings, holidays aside';
    node.dataset.marketStatus = clock.open ? 'open' : 'closed';
  });
  root.querySelectorAll('[data-market-status]').forEach(node => {
    node.dataset.marketStatus = clock.open ? 'open' : 'closed';
    node.classList.toggle('is-open', clock.open);
    node.classList.toggle('is-closed', !clock.open);
  });
  return clock;
}

if (typeof document !== 'undefined') {
  let timer;
  function schedule() {
    window.clearTimeout(timer);
    if (document.hidden) return;
    updateMarketClock();
    timer = window.setTimeout(schedule, 60_000 - Date.now() % 60_000 + 20);
  }
  schedule();
  document.addEventListener('visibilitychange', schedule);
  window.addEventListener('pageshow', schedule);
  window.addEventListener('pagehide', () => window.clearTimeout(timer));
}
