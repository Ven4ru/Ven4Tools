// Скрипт: находит все приложения в master.json без chocoId и/или scoopId
// Вывод: таблица с предложенными ID для ручного поиска на chocolatey.org и scoop.sh

import { readFileSync } from 'fs';

const master = JSON.parse(readFileSync('Catalog/master.json', 'utf8'));

// Предложенные ID на основе известных пакетов (требуют проверки)
const chocoSuggestions = {
  'firefox':                 'firefox',
  'google-chrome':           'googlechrome',
  'microsoft-edge':          'microsoft-edge',
  'opera':                   'opera',
  'brave':                   'brave',
  'yandex-browser':          '?',
  'librewolf':               'librewolf',
  'onlyoffice':              'onlyoffice',
  'adobe-acrobat-reader':    'adobereader',
  'sumatra-pdf':             'sumatrapdf',
  'drawio':                  'drawio',
  'foxit-pdf-reader':        'foxitreader',
  'libreoffice':             'libreoffice',
  'obsidian':                'obsidian',
  'notion':                  'notion',
  'gimp':                    'gimp',
  'krita':                   'krita',
  'blender':                 'blender',
  'inkscape':                'inkscape',
  'paint-net':               'paint.net',
  'lunacy':                  '?',
  'xnview':                  'xnviewmp',
  'vscode':                  'vscode',
  'visual-studio-community': 'visualstudio2022community',
  'python':                  'python3',
  'nodejs':                  'nodejs',
  'git':                     'git',
  'docker-desktop':          'docker-desktop',
  'postman':                 'postman',
  'notepad-plus-plus':       'notepadplusplus',
  'github-desktop':          'github-desktop',
  'putty':                   'putty',
  'telegram':                'telegram',
  'discord':                 'discord',
  'slack':                   'slack',
  'zoom':                    'zoom',
  'microsoft-teams':         'microsoft-teams',
  'element':                 'element-desktop',
  'whatsapp':                'whatsapp',
  'vlc':                     'vlc',
  'yandex-music':            '?',
  'obs-studio':              'obs-studio',
  'handbrake':               'handbrake',
  'audacity':                'audacity',
  'foobar2000':              'foobar2000',
  'mpc-be':                  'mpc-be',
  '7zip':                    '7zip',
  'winrar':                  'winrar',
  'everything':              'everything',
  'crystaldiskinfo':         'crystaldiskinfo',
  'crystaldiskmark':         'crystaldiskmark',
  'greenshot':               'greenshot',
  'sharex':                  'sharex',
  'yandex-disk':             '?',
  'powertoys':               'powertoys',
  'rufus':                   'rufus',
  'steam':                   'steam',
  'itch':                    '?',
  'playnite':                'playnite',
  'gog-galaxy':              'goggalaxy',
  'snappy-driver':           '?',
  'ddu':                     'display-driver-uninstaller',
  'nvcleanstall':            '?',
  'malwarebytes':            'malwarebytes',
  'aida64':                  'aida64',
  'hwmonitor':               'hwmonitor',
  'rclone':                  'rclone',
  'f.lux':                   'flux',
  'autohotkey':              'autohotkey',
  'wiztree':                 'wiztree',
  'cpu-z':                   'cpu-z',
};

const scoopSuggestions = {
  'firefox':                 'firefox  [extras]',
  'google-chrome':           'googlechrome  [extras]',
  'microsoft-edge':          '?',
  'opera':                   'opera  [extras]',
  'brave':                   'brave  [extras]',
  'yandex-browser':          '?',
  'librewolf':               'librewolf  [extras]',
  'onlyoffice':              'onlyoffice  [extras]',
  'adobe-acrobat-reader':    'acrobatreader  [extras]',
  'sumatra-pdf':             'sumatrapdf  [main]',
  'drawio':                  'draw.io  [extras]',
  'foxit-pdf-reader':        '?',
  'libreoffice':             'libreoffice  [extras]',
  'obsidian':                'obsidian  [extras]',
  'notion':                  'notion  [extras]',
  'gimp':                    'gimp  [extras]',
  'krita':                   'krita  [extras]',
  'blender':                 'blender  [main]',
  'inkscape':                'inkscape  [extras]',
  'paint-net':               'paint.net  [extras]',
  'lunacy':                  'lunacy  [extras]',
  'xnview':                  'xnviewmp  [extras]',
  'vscode':                  'vscode  [extras]',
  'visual-studio-community': '?',
  'python':                  'python  [main]',
  'nodejs':                  'nodejs  [main]',
  'git':                     'git  [main]',
  'docker-desktop':          'docker-desktop  [extras]',
  'postman':                 'postman  [extras]',
  'notepad-plus-plus':       'notepadplusplus  [extras]',
  'github-desktop':          'github  [extras]',
  'putty':                   'putty  [main]',
  'telegram':                'telegram  [extras]',
  'discord':                 'discord  [extras]',
  'slack':                   'slack  [extras]',
  'zoom':                    'zoom  [extras]',
  'microsoft-teams':         'teams  [extras]',
  'element':                 'element  [extras]',
  'whatsapp':                'whatsapp  [extras]',
  'vlc':                     'vlc  [extras]',
  'yandex-music':            '?',
  'obs-studio':              'obs-studio  [extras]',
  'handbrake':               'handbrake  [extras]',
  'audacity':                'audacity  [extras]',
  'foobar2000':              'foobar2000  [extras]',
  'mpc-be':                  'mpc-be  [extras]',
  '7zip':                    '7zip  [main]',
  'winrar':                  'winrar  [main]',
  'everything':              'everything  [extras]',
  'crystaldiskinfo':         'crystaldiskinfo  [extras]',
  'crystaldiskmark':         'crystaldiskmark  [extras]',
  'greenshot':               'greenshot  [extras]',
  'sharex':                  'sharex  [extras]',
  'yandex-disk':             '?',
  'powertoys':               'powertoys  [extras]',
  'rufus':                   'rufus  [extras]',
  'steam':                   'steam  [extras]',
  'itch':                    'itch  [extras]',
  'playnite':                'playnite  [extras]',
  'gog-galaxy':              'gog-galaxy  [extras]',
  'snappy-driver':           '?',
  'ddu':                     'ddu  [extras]',
  'nvcleanstall':            'nvcleanstall  [extras]',
  'malwarebytes':            'malwarebytes  [extras]',
  'aida64':                  '?',
  'hwmonitor':               'hwmonitor  [extras]',
  'rclone':                  'rclone  [main]',
  'f.lux':                   'flux  [extras]',
  'autohotkey':              'autohotkey  [extras]',
  'wiztree':                 'wiztree  [extras]',
  'cpu-z':                   'cpu-z  [extras]',
};

const missing = [];
for (const app of master.apps) {
  const noChoco = !app.chocoId;
  const noScoop = !app.scoopId;
  if (noChoco || noScoop) {
    missing.push({
      id: app.id,
      name: app.name,
      category: app.category,
      wingetId: app.wingetId,
      chocoMissing: noChoco,
      scoopMissing: noScoop,
      chocoSuggestion: noChoco ? (chocoSuggestions[app.id] ?? '?') : app.chocoId,
      scoopSuggestion: noScoop ? (scoopSuggestions[app.id] ?? '?') : app.scoopId,
    });
  }
}

console.log(`\nПриложений без chocoId и/или scoopId: ${missing.length} из ${master.apps.length}\n`);
console.log('Пометки: [нужна проверка] = предположение, требует верификации на сайте');
console.log('         ? = ID неизвестен, найдите вручную\n');
console.log('='.repeat(110));
console.log(`${'App ID'.padEnd(28)} ${'Choco (chocolatey.org)'.padEnd(38)} ${'Scoop (scoop.sh)'.padEnd(40)}`);
console.log('='.repeat(110));

const needManual = [];
for (const a of missing) {
  const choco = a.chocoMissing ? a.chocoSuggestion : `[уже есть: ${a.chocoSuggestion}]`;
  const scoop  = a.scoopMissing  ? a.scoopSuggestion  : `[уже есть: ${a.scoopSuggestion}]`;
  console.log(`${a.id.padEnd(28)} ${choco.padEnd(38)} ${scoop}`);
  if (a.chocoSuggestion === '?' || a.scoopSuggestion === '?') needManual.push(a.id);
}

console.log('='.repeat(110));
if (needManual.length > 0) {
  console.log(`\nТребуют ручного поиска (знак ?): ${needManual.join(', ')}`);
}
console.log('\nСсылки:');
console.log('  Chocolatey: https://community.chocolatey.org/packages');
console.log('  Scoop main: https://github.com/ScoopInstaller/Main/tree/master/bucket');
console.log('  Scoop extras: https://github.com/ScoopInstaller/Extras/tree/master/bucket');
