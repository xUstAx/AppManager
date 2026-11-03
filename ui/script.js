const tabsEl = document.getElementById('tabs');
const appsEl = document.getElementById('apps');
const categoryMenu = document.getElementById('categoryMenu');
const settingsOverlay = document.getElementById('settingsOverlay');
const themeScope = document.getElementById('themeScope');
const colorFields = document.getElementById('colorFields');

const defaultColors = {
  toolbarBackground: '#2A2A2A',
  tabsBackground: '#303030',
  appsBackground: '#1E1E1E',
  cardBackground: '#262626',
  buttonBackground: '#3B6EA5',
  buttonHoverBackground: '#4B86C5',
  dangerButtonBackground: '#713B3B',
  textColor: '#E8E8E8',
  mutedTextColor: '#AAAAAA',
  borderColor: '#454545'
};
const colorLabels = {
  toolbarBackground: 'Панель инструментов',
  tabsBackground: 'Полоса каталогов',
  appsBackground: 'Поле приложений',
  cardBackground: 'Карточки приложений',
  buttonBackground: 'Кнопки',
  buttonHoverBackground: 'Кнопки при наведении',
  dangerButtonBackground: 'Кнопки удаления',
  textColor: 'Основной текст',
  mutedTextColor: 'Вторичный текст',
  borderColor: 'Разделители и обводки'
};
const cssVariables = {
  toolbarBackground: '--toolbar-bg',
  tabsBackground: '--tabs-bg',
  appsBackground: '--apps-bg',
  cardBackground: '--card-bg',
  buttonBackground: '--button-bg',
  buttonHoverBackground: '--button-hover-bg',
  dangerButtonBackground: '--danger-button-bg',
  textColor: '--text-color',
  mutedTextColor: '--muted-text-color',
  borderColor: '--border-color'
};

let state = { categories: [], apps: {}, iconRoot: '', settings: { defaults: defaultColors, categoryThemes: {}, pinnedCategories: [] } };
let currentCategory = null;
let draggedCategory = null;
let suppressNextClick = false;
let editingColors = { ...defaultColors };
const placeholderIcon = 'data:image/svg+xml;utf8,<svg xmlns="http://www.w3.org/2000/svg" width="56" height="56"><rect width="56" height="56" fill="%23555" rx="8"/></svg>';

window.chrome.webview.addEventListener('message', ev => {
  const msg = ev.data;
  if (msg.action === 'state') {
    state = msg.payload;
    state.settings ||= { defaults: { ...defaultColors }, categoryThemes: {}, pinnedCategories: [] };
    state.settings.defaults ||= { ...defaultColors };
    state.settings.categoryThemes ||= {};
    state.settings.pinnedCategories ||= [];
    if (!currentCategory || !state.categories.includes(currentCategory))
      currentCategory = state.categories[0] || null;
    render();
  } else if (msg.action === 'themeSaved') {
    closeSettings();
  }
});

function send(action, extra = {}) {
  window.chrome.webview.postMessage({ action, ...extra });
}

function isPinned(category) {
  return state.settings.pinnedCategories.some(item => item.toLowerCase() === category.toLowerCase());
}

function render() {
  renderTabs();
  applyTheme();
  renderApps();
}

function renderTabs() {
  tabsEl.replaceChildren();
  const hasPinnedCategories = state.categories.some(isPinned);
  let lastPinnedTab = null;

  state.categories.forEach(category => {
    const pinned = isPinned(category);
    const tab = document.createElement('button');
    tab.type = 'button';
    tab.className = 'tab' + (category === currentCategory ? ' active' : '') + (pinned ? ' pinned' : '');
    tab.draggable = true;
    tab.title = 'Правый клик — закрепить или открепить. Перетащите для сортировки.';
    tab.textContent = category;
    tab.onclick = () => {
      if (suppressNextClick) {
        suppressNextClick = false;
        return;
      }
      currentCategory = category;
      render();
    };
    tab.oncontextmenu = event => {
      event.preventDefault();
      showCategoryMenu(event.clientX, event.clientY, category);
    };
    tab.ondragstart = event => {
      draggedCategory = category;
      document.body.classList.add('category-dragging');
      event.dataTransfer.effectAllowed = 'move';
      event.dataTransfer.setData('text/plain', category);
    };
    tab.ondragover = event => {
      event.preventDefault();
      const source = draggedCategory || event.dataTransfer.getData('text/plain');
      const sameGroup = source && isPinned(source) === pinned;
      event.dataTransfer.dropEffect = sameGroup ? 'move' : 'none';
      clearDropIndicators();
      if (sameGroup && source !== category) {
        const midpoint = tab.getBoundingClientRect().left + tab.getBoundingClientRect().width / 2;
        tab.classList.add(event.clientX < midpoint ? 'drop-before' : 'drop-after');
      }
    };
    tab.ondrop = event => {
      event.preventDefault();
      const source = draggedCategory || event.dataTransfer.getData('text/plain');
      if (!source || source === category || isPinned(source) !== pinned) return;
      const midpoint = tab.getBoundingClientRect().left + tab.getBoundingClientRect().width / 2;
      reorderCategory(source, category, event.clientX >= midpoint);
    };
    tab.ondragend = () => {
      draggedCategory = null;
      document.body.classList.remove('category-dragging');
      suppressNextClick = true;
      window.setTimeout(() => { suppressNextClick = false; }, 250);
    };
    tabsEl.appendChild(tab);
    if (pinned) lastPinnedTab = tab;
  });

  if (hasPinnedCategories && lastPinnedTab) {
    const pinnedEndDrop = createEndDropZone('Закреплённые каталоги');
    lastPinnedTab.after(pinnedEndDrop);
    if (state.categories.some(category => !isPinned(category))) {
      const separator = document.createElement('span');
      separator.className = 'tab-separator';
      separator.setAttribute('aria-hidden', 'true');
      pinnedEndDrop.after(separator);
    }
  }

  const endDrop = createEndDropZone('Обычные каталоги');
  endDrop.classList.add('tab-end-drop');
  tabsEl.appendChild(endDrop);
}

function clearDropIndicators() {
  tabsEl.querySelectorAll('.drop-before, .drop-after, .drop-active').forEach(element => {
    element.classList.remove('drop-before', 'drop-after', 'drop-active');
  });
}

function createEndDropZone(groupLabel) {
  const zone = document.createElement('div');
  const isPinnedZone = groupLabel === 'Закреплённые каталоги';
  zone.className = `tab-drop-zone${isPinnedZone ? ' pinned-end-drop' : ''}`;
  zone.title = `Перетащите сюда, чтобы поместить каталог в конец группы «${groupLabel}»`;
  zone.ondragover = event => {
    event.preventDefault();
    const source = draggedCategory || event.dataTransfer.getData('text/plain');
    const allowed = source && isPinned(source) === isPinnedZone;
    event.dataTransfer.dropEffect = allowed ? 'move' : 'none';
    clearDropIndicators();
    if (allowed) zone.classList.add('drop-active');
  };
  zone.ondrop = event => {
    event.preventDefault();
    const source = draggedCategory || event.dataTransfer.getData('text/plain');
    if (!source || isPinned(source) !== isPinnedZone) return;
    const group = state.categories.filter(category => isPinned(category) === isPinnedZone);
    const target = group[group.length - 1];
    if (target) reorderCategory(source, target, true);
    else clearDropIndicators();
  };
  if (!isPinnedZone) zone.classList.add('tab-end-drop');
  return zone;
}

function reorderCategory(source, target, insertAfter) {
  const pinned = isPinned(source);
  if (source === target || pinned !== isPinned(target)) return;

  const group = state.categories.filter(category => isPinned(category) === pinned);
  const sourceIndex = group.indexOf(source);
  const targetIndex = group.indexOf(target);
  if (sourceIndex < 0 || targetIndex < 0) return;

  group.splice(sourceIndex, 1);
  const adjustedTargetIndex = group.indexOf(target);
  const insertIndex = adjustedTargetIndex + (insertAfter ? 1 : 0);
  group.splice(insertIndex, 0, source);

  const otherGroup = state.categories.filter(category => isPinned(category) !== pinned);
  state.categories = pinned ? [...group, ...otherGroup] : [...otherGroup, ...group];
  clearDropIndicators();
  suppressNextClick = true;
  render();
  send('saveCategoryOrder', { categories: state.categories });
  window.setTimeout(() => { suppressNextClick = false; }, 250);
}

function renderApps() {
  appsEl.replaceChildren();
  const list = (currentCategory && state.apps[currentCategory]) || [];
  if (list.length === 0) {
    const empty = document.createElement('div');
    empty.className = 'empty';
    empty.textContent = 'Нет приложений в этом каталоге';
    appsEl.appendChild(empty);
    return;
  }

  list.forEach(app => {
    const card = document.createElement('div');
    card.className = 'app';
    card.title = app.exePath;

    const img = document.createElement('img');
    img.src = app.iconPath ? fileUrl(app) : placeholderIcon;
    img.onerror = () => {
      img.onerror = null;
      img.src = placeholderIcon;
    };
    card.appendChild(img);

    const label = document.createElement('div');
    label.className = 'label';
    label.textContent = app.displayName || app.folderName;
    card.appendChild(label);

    const menuButton = document.createElement('button');
    menuButton.className = 'app-menu-button';
    menuButton.type = 'button';
    menuButton.textContent = '⋮';
    menuButton.title = 'Действия с приложением';

    const menu = document.createElement('div');
    menu.className = 'app-menu';
    menu.hidden = true;
    menu.onclick = event => event.stopPropagation();
    menuButton.onclick = event => {
      event.stopPropagation();
      document.querySelectorAll('.app-menu').forEach(openMenu => {
        if (openMenu !== menu) openMenu.hidden = true;
      });
      menu.hidden = !menu.hidden;
    };

    const categorySelect = document.createElement('select');
    categorySelect.setAttribute('aria-label', 'Переместить в каталог');
    state.categories.filter(category => category !== app.category).forEach(category => {
      const option = document.createElement('option');
      option.value = category;
      option.textContent = category;
      categorySelect.appendChild(option);
    });

    const moveButton = document.createElement('button');
    moveButton.type = 'button';
    moveButton.textContent = 'Переместить';
    moveButton.disabled = categorySelect.options.length === 0;
    moveButton.onclick = () => {
      if (categorySelect.value)
        send('moveApp', {
          category: app.category || currentCategory,
          folderName: app.folderName,
          targetCategory: categorySelect.value
        });
    };

    const deleteButton = document.createElement('button');
    deleteButton.type = 'button';
    deleteButton.className = 'danger';
    deleteButton.textContent = 'Удалить';
    deleteButton.onclick = () => send('deleteApp', {
      category: app.category || currentCategory,
      folderName: app.folderName
    });

    menu.append(categorySelect, moveButton, deleteButton);
    card.append(menuButton, menu);
    card.onclick = () => send('launchApp', { exe: app.exePath });
    appsEl.appendChild(card);
  });
}

function applyTheme() {
  const defaults = { ...defaultColors, ...state.settings.defaults };
  const theme = currentCategory
    ? { ...defaults, ...(state.settings.categoryThemes[currentCategory] || {}) }
    : defaults;
  Object.entries(cssVariables).forEach(([key, variable]) => {
    document.documentElement.style.setProperty(variable, theme[key]);
  });
}

function fileUrl(app) {
  const category = encodeURIComponent(app.category || currentCategory || '');
  const folder = encodeURIComponent(app.folderName || '');
  return `${state.iconRoot}/${category}/${folder}/icon.png`;
}

function showCategoryMenu(x, y, category) {
  categoryMenu.replaceChildren();
  const pinButton = document.createElement('button');
  pinButton.textContent = isPinned(category) ? 'Открепить каталог' : 'Закрепить каталог';
  pinButton.onclick = () => {
    send('setCategoryPinned', { category, pinned: !isPinned(category) });
    categoryMenu.hidden = true;
  };
  categoryMenu.appendChild(pinButton);
  categoryMenu.hidden = false;
  categoryMenu.style.left = `${Math.max(0, Math.min(x, window.innerWidth - 190))}px`;
  categoryMenu.style.top = `${Math.max(0, Math.min(y, window.innerHeight - 55))}px`;
}

function openSettings() {
  themeScope.replaceChildren();
  const defaultsOption = document.createElement('option');
  defaultsOption.value = '';
  defaultsOption.textContent = 'Цвета по умолчанию';
  themeScope.appendChild(defaultsOption);
  state.categories.forEach(category => {
    const option = document.createElement('option');
    option.value = category;
    option.textContent = category;
    themeScope.appendChild(option);
  });
  themeScope.value = currentCategory || '';
  loadThemeEditor();
  settingsOverlay.hidden = false;
}

function loadThemeEditor() {
  const category = themeScope.value;
  editingColors = {
    ...defaultColors,
    ...state.settings.defaults,
    ...(category ? state.settings.categoryThemes[category] || {} : {})
  };
  colorFields.replaceChildren();
  Object.entries(colorLabels).forEach(([key, labelText]) => {
    const row = document.createElement('label');
    row.className = 'color-field';
    const text = document.createElement('span');
    text.textContent = labelText;
    const picker = document.createElement('input');
    picker.type = 'color';
    picker.value = editingColors[key];
    picker.setAttribute('aria-label', labelText);
    picker.oninput = () => {
      editingColors[key] = picker.value.toUpperCase();
      applyEditorPreview();
    };
    row.append(text, picker);
    colorFields.appendChild(row);
  });
  document.getElementById('btnResetTheme').hidden = !category ||
    !state.settings.categoryThemes[category];
  applyEditorPreview();
}

function applyEditorPreview() {
  const theme = { ...state.settings.defaults, ...editingColors };
  Object.entries(cssVariables).forEach(([key, variable]) => {
    document.documentElement.style.setProperty(variable, theme[key]);
  });
}

function closeSettings() {
  settingsOverlay.hidden = true;
  applyTheme();
}

document.getElementById('btnAddCategory').onclick = () => {
  const name = prompt('Название каталога (латиница):');
  if (name && name.trim()) send('createCategory', { name: name.trim() });
};

document.getElementById('btnAddApp').onclick = () => {
  if (state.categories.length === 0) {
    alert('Сначала создайте каталог.');
    return;
  }
  send('addApp', { category: currentCategory });
};

document.getElementById('btnOpenCatalog').onclick = () => {
  if (!currentCategory) {
    alert('Сначала создайте или выберите каталог.');
    return;
  }
  send('viewCategory', { category: currentCategory });
};

document.getElementById('btnSettings').onclick = openSettings;
document.getElementById('btnCloseSettings').onclick = closeSettings;
document.getElementById('btnCancelSettings').onclick = closeSettings;
document.getElementById('btnSaveTheme').onclick = () => {
  send('saveTheme', { category: themeScope.value || null, colors: editingColors });
};
document.getElementById('btnResetTheme').onclick = () => {
  if (themeScope.value)
    send('resetCategoryTheme', { category: themeScope.value });
};
themeScope.onchange = loadThemeEditor;
settingsOverlay.addEventListener('click', event => {
  const dialog = settingsOverlay.querySelector('.settings-dialog');
  if (dialog && event.target instanceof Node && !dialog.contains(event.target))
    closeSettings();
});
document.addEventListener('click', event => {
  if (!categoryMenu.contains(event.target)) categoryMenu.hidden = true;
  if (!event.target.closest('.app-menu, .app-menu-button')) {
    document.querySelectorAll('.app-menu').forEach(menu => { menu.hidden = true; });
  }
});
document.addEventListener('keydown', event => {
  if (event.key === 'Escape') {
    categoryMenu.hidden = true;
    if (!settingsOverlay.hidden) closeSettings();
  }
});

send('getState');
