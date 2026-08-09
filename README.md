# FluidHUD

Компактный media HUD для Windows 10/11. FluidHUD показывает текущий трек поверх остальных окон, не забирает фокус, управляется глобальной горячей клавишей и автоматически исчезает после заданного времени.

**Автор:** CatYaderka  
**Стек:** C# · .NET 8 · WinUI 3 · Windows App SDK 2.0.1

## Возможности

- получение активной media session через GSMTC;
- название, исполнитель, альбом и обложка текущего трека;
- Play/Pause поверх обложки при наведении;
- Previous/Next по сторонам progress bar;
- перемотка кликом и перетаскиванием seek thumb;
- непрерывный marquee для длинных названий без пустого участка;
- глобальная горячая клавиша через `RegisterHotKey` и `WM_HOTKEY`;
- прозрачность HUD от 20 до 100%;
- настройка времени показа и скорости spring-анимации;
- master volume/mute через Windows Core Audio;
- запуск вместе с Windows для текущего пользователя;
- пятисекундный grace period при переключении треков;
- отложенное обновление обложек для проигрывателей, публикующих thumbnail не сразу;
- один долгоживущий HWND без создания дубликатов при media events;
- self-contained Release в одном `FluidHUD.exe`.

## Почему HUD не использует системный Acrylic

Window-level Acrylic и Mica рендерятся DWM отдельно от XAML. На короткоживущем overlay это приводит к неприятному артефакту: прозрачная или размытая поверхность HWND может появиться раньше самой карточки.

FluidHUD использует прозрачный composition backdrop и собственную XAML-поверхность. Перед первым кадром HWND временно cloaked, non-client rendering отключён, а затем карточка открывается уже вместе с запущенной анимацией. Это немного менее «мыльно», зато предсказуемо работает на разных версиях Windows и не создаёт фантомных рамок.

## Системные требования

### Для запуска

- Windows 10 2004 или новее;
- Windows 11 рекомендуется;
- x64 по умолчанию, также поддерживаются x86 и ARM64.

### Для сборки

- Windows;
- .NET 8 SDK;
- доступ к NuGet.org при первом restore;
- PowerShell 5.1 или PowerShell 7.

## Сборка

Проект публикуется только в Release. Скрипт создаёт self-contained single-file executable и проверяет, что рядом с ним не осталось внешних runtime-файлов.

```powershell
Set-ExecutionPolicy -Scope Process Bypass -Force
.\build.ps1
```

Результат:

```text
release\FluidHUD.exe
```

Другие архитектуры:

```powershell
.\build.ps1 -Platform x86
.\build.ps1 -Platform ARM64
```

Single-file сборка содержит .NET runtime, Windows App SDK runtime, native-библиотеки и ресурсы. `EnableMsixTooling=true` используется только для встраивания `resources.pri`; приложение при этом остаётся unpackaged (`WindowsPackageType=None`, `AppxPackage=false`). При запуске часть native-содержимого может извлекаться механизмом .NET bundle во временный системный каталог — распространять нужно только один EXE.

## Первый запуск

1. Запустите `FluidHUD.exe`.
2. Нажмите на поле горячей клавиши и введите сочетание, например `Ctrl + ~`.
3. Настройте прозрачность, длительность и скорость появления.
4. При необходимости включите «Запуск вместе с Windows».
5. Нажмите «Начать».

Настройки сохраняются здесь:

```text
%LocalAppData%\FluidHUD\settings.json
```

Повреждённый JSON не ломает запуск: файл переносится в карантин, после чего загружаются безопасные значения по умолчанию.

## Автозапуск

Автозапуск регистрируется без прав администратора:

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Run
```

Значение обновляется при запуске и после сохранения настроек, поэтому перенос EXE в другую папку корректно меняет зарегистрированный путь.

## Управление

- горячая клавиша — показать или скрыть HUD;
- наведение на обложку — показать Play/Pause;
- клик по progress bar — перейти к выбранной позиции;
- drag seek thumb — плавно выбрать позицию;
- Previous/Next — переключить трек;
- шестерёнка — открыть настройки;
- кнопка питания в настройках — полностью завершить FluidHUD.

Если HUD уже открыт, изменение громкости или трека не запускает show-анимацию повторно. Обновляются данные и продлевается auto-hide timer. Если в этот момент уже начался Fade Out, он отменяется на том же окне.

## Архитектура

```text
src/FluidHUD/
├─ Controls/       MarqueeText и SeekBar
├─ Converters/     XAML converters
├─ Interop/        Win32, DWM и window helpers
├─ Models/         настройки, хоткей и media snapshot
├─ Services/       GSMTC, volume, startup, cache, JSON, color
├─ ViewModels/     состояние HUD и timeline interpolation
├─ Views/          overlay и onboarding/settings
├─ Assets/         иконка и noise texture
├─ App.xaml.cs     жизненный цикл и координация сервисов
└─ FluidHUD.csproj
```

Ключевые решения:

- media callbacks переводятся в UI thread через `DispatcherQueue`;
- устаревшие async refresh отменяются;
- при временно пустой session прошлый snapshot удерживается не менее пяти секунд;
- поздняя обложка может обновиться без повторной смены трека;
- LRU-кэш ограничен 32 МБ и 40 элементами;
- progress интерполируется между системными timeline events;
- seek отправляется через `TryChangePlaybackPositionAsync` в ticks;
- настройки записываются атомарно через временный файл.

## Ограничения

FluidHUD видит только приложения, публикующие GSMTC session. Старые проигрыватели без системной media integration не поддерживаются.

Некоторые источники, особенно live streams, не разрешают перемотку. В таком случае seek bar остаётся неактивным или проигрыватель отклоняет команду.

Topmost HUD работает поверх обычных и borderless-fullscreen приложений. Exclusive fullscreen, secure desktop/UAC и защищённый видеопуть могут перекрывать обычное desktop-окно — это ограничение Windows.

## Диагностика

Лог необработанных ошибок:

```text
%LocalAppData%\FluidHUD\FluidHUD.log
```

Перед повторной сборкой после обновления исходников рекомендуется удалить старые артефакты:

```powershell
Remove-Item .\src\FluidHUD\bin, .\src\FluidHUD\obj, .\release `
    -Recurse -Force -ErrorAction SilentlyContinue
```

## Конфиденциальность

FluidHUD не отправляет данные в сеть. Название, исполнитель, timeline и обложка читаются локально из Windows media session. Сеть используется только NuGet во время сборки.

## Лицензия

MIT. Свободно используйте, изменяйте и распространяйте проект с сохранением текста лицензии.
