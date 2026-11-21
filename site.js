const panelVideo = document.querySelector('#panel-demo');
const panelButton = document.querySelector('.motion-button');
const reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');

function updateMotionButton() {
  panelButton.textContent = panelVideo.paused ? 'Воспроизвести' : 'Пауза';
}

function playPanel() {
  panelVideo.play().catch(updateMotionButton);
}

panelVideo.controls = false;
panelButton.hidden = false;
panelVideo.addEventListener('play', updateMotionButton);
panelVideo.addEventListener('pause', updateMotionButton);
panelButton.addEventListener('click', () => {
  if (panelVideo.paused) playPanel();
  else panelVideo.pause();
});

if (!reducedMotion.matches && !navigator.connection?.saveData) playPanel();
reducedMotion.addEventListener('change', event => {
  if (event.matches) panelVideo.pause();
});

for (const switcher of document.querySelectorAll('[data-switcher]')) {
  const tablist = switcher.querySelector('[role="tablist"]');
  const tabs = Array.from(tablist.querySelectorAll('[role="tab"]'));
  const panels = tabs.map(tab => document.getElementById(tab.getAttribute('aria-controls')));

  function selectTab(index) {
    tabs.forEach((tab, current) => {
      const selected = current === index;
      tab.setAttribute('aria-selected', String(selected));
      tab.tabIndex = selected ? 0 : -1;
      panels[current].hidden = !selected;
      if (!selected) panels[current].querySelector('video')?.pause();
    });
  }

  tabs.forEach((tab, index) => {
    panels[index].setAttribute('role', 'tabpanel');
    panels[index].setAttribute('aria-labelledby', tab.id);
    panels[index].tabIndex = 0;
    tab.addEventListener('click', () => selectTab(index));
    tab.addEventListener('keydown', event => {
      const destinations = { ArrowLeft: index - 1, ArrowRight: index + 1, Home: 0, End: tabs.length - 1 };
      if (!Object.hasOwn(destinations, event.key)) return;
      event.preventDefault();
      const next = (destinations[event.key] + tabs.length) % tabs.length;
      selectTab(next);
      tabs[next].focus();
    });
  });

  selectTab(0);
  tablist.hidden = false;
}
