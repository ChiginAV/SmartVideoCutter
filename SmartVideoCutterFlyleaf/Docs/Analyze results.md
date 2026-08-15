Анализ завершён — я прочитал все исходники `SmartVideoCutterFlyleaf`, сравнил с «родственными» проектами (WinForms
`SmartVideoCutter` и `SmartVideoCutterVLC`) и проверил актуальный API FlyleafLib 3.10 (включая официальный MVVM-пример).
Вот полная картина и план улучшений.

---

# Анализ проекта SmartVideoCutterFlyleaf

## 1. Что есть сейчас

WPF (.NET 10, x64) + MVVM (CommunityToolkit.Mvvm) + FlyleafLib 3.10.4 (FFmpeg/DirectX-плеер) + ONNX Runtime DirectML.
Реализовано:

- открытие видео, Play/Pause, слайдер позиции, громкость;
- «Analyze» — получение ключевых кадров через **внешний** `ffprobe.exe`;
- перемотка по двойному клику на ключевом кадре;
- настройки (пути к YOLO/ArcFace/FFmpeg) в `settings.json`;
- `YoloFaceDetector` — **заготовка**: открывает ONNX-сессию, но метода `Detect` нет (детекция закомментирована в
  `MainViewModel`);
- `ExportFile` — **пустая команда**.

## 2. Критические баги (фиксить в первую очередь)

| #  | Баг                                                                                                                                                                                                                                                                                                                                                                    | Где                                                                |
|----|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|--------------------------------------------------------------------|
| 1  | **Сбой единиц при перемотке на ключевой кадр.** `Keyframe.Timestamp` хранится в **мс** (`ptsTime * 1000`), а `Player.CurTime` в Flyleaf — в **тиках 100нс** (в исходниках: `ReSync(..., (int)(CurTime / 10000), ...)`). `SeekToTimestamp` передаёт мс напрямую в `CurTime` → перемотка улетает в начало (10 с → 1 мс). Нужно `Timestamp * 10000` или `Player.Seek(ms)` | `MainViewModel.SeekToTimestamp` → `MediaPlayerService.SetPosition` |
| 2  | **NullReferenceException в `AnalyzeFile`**: `_filePath` не инициализирован (`private string _filePath;` при `Nullable=enable`) и не проверяется. Кнопка Analyze активна без открытого файла                                                                                                                                                                            | `MainViewModel.cs:16,128`                                          |
| 3  | `KeyframeList` = `null` до первого анализа (не инициализирован пустым списком); `Keyframe` не реализует `INotifyPropertyChanged` → чекбокс `IsSelected` в DataGrid не обновляется                                                                                                                                                                                      | `MainViewModel.cs:26`, `Models/Keyframe.cs`                        |
| 4  | **Нет обработки ошибок ffprobe**: пустой/неверный `FfmpegPath` → падение `Process.Start` без сообщения; нет проверки `ExitCode`, таймаута, чтения `StandardError`                                                                                                                                                                                                      | `FFmpegService.cs`                                                 |
| 5  | `double.Parse` без `InvariantCulture` в `GetVideoInfo` (локаль с запятой сломает FPS); деление на ноль при `r_frame_rate = "0/0"` (VFR)                                                                                                                                                                                                                                | `FFmpegService.cs:42`                                              |
| 6  | `VideoInfo` никогда не заполняется (вызов закомментирован в `LoadMedia`) — свойство всегда `null`                                                                                                                                                                                                                                                                      | `MediaPlayerService.cs:91`                                         |
| 7  | `MaxPosition = _flyleafPlayer.Duration` читается один раз сразу после `Open()` — если длительность ещё не известна, слайдер останется с `Maximum=0`. Надёжнее подписаться на `Player.PropertyChanged` (`Duration`, `CurTime` — Flyleaf сам обновляет их на UI-потоке при `UIRefresh=true`, что уже включено!)                                                          | `MediaPlayerService.cs:94`                                         |
| 8  | `Engine.Start()` вызывается, но **движок никогда не останавливается** при выходе (`FlyleafLib.Engine.Stop()` существует)                                                                                                                                                                                                                                               | `MediaPlayerService.Initialize/Dispose`                            |
| 9  | Финализатор `~MediaPlayerService()` не освобождает неуправляемые ресурсы (плеер) — либо убрать, либо сделать корректным                                                                                                                                                                                                                                                | `MediaPlayerService.cs:190`                                        |
| 10 | `OpenFile`/`AnalyzeFile` объявлены `async void` без `await`/`try-catch` — любое исключение роняет UI-поток                                                                                                                                                                                                                                                             | `MainViewModel.cs`                                                 |

## 3. Отсутствующая функциональность (главные «дыры»)

1. **Детекция лиц не портирована.** В WinForms-проекте полный пайплайн: `TensorConverter` (переиспользуемые буферы) →
   `YoloFaceDetector.Detect` → `YoloResultParser` (sigmoid, NCHW/NHWC) → `NonMaxSuppression`. В Flyleaf-проекте из этого
   только «голая» сессия. План порта:
    - захват кадра — `Player.TakeSnapshotToBitmapSource()` (готовый WPF `BitmapSource`, без файловых снимков как в
      VLC-версии) → `Mat` через `CopyPixels`;
    - инференс в `Task.Run` с «single-flight» (пропуск, если предыдущий ещё выполняется) и троттлингом ~1 кадр/с;
    - отрисовка рамок — **overlay-контент `FlyleafHost`** (Canvas с Rectangle в координатах видео, масштабирование под
      `Player.Video.Width/Height`);
    - ленивая загрузка модели (сейчас конструктор детектора в VM закомментирован, а при включении модель будет грузиться
      при старте даже если пользователь не будет анализировать).
2. **Экспорт (ядро «cutter»!) пустой.** В WinForms-проекте есть `VideoProcessor` (ffprobe/ffmpeg), `FfmpegEncoder`
   (нарезка сегментов), `VideoExporterService` (temp-папка с GUID, list-файл, склейка, гарантированная очистка), модель
   `VideoSegment`. Всё это нужно портировать, плюс UI: выбор сегментов (уже есть чекбоксы в DataGrid), `SaveFileDialog`,
   прогресс с отменой (`CancellationToken`).
3. **Распознавание лиц (ArcFace)** — в настройках есть путь, кода нет. Это «умная» часть продукта (найти сегменты с
   конкретным человеком). Портировать `ArcFaceRecognizer` + `FaceSimilarityCalculator` + логику `VideoAnalyzerService` —
   как этап 2.
4. **Offline-анализ через Flyleaf Extractor** — библиотека умеет извлекать кадры с шагом N (см. пример
   FlyleafExtractor). Для анализа всего видео это быстрее и правильнее, чем покадровые снимки из плеера.

## 4. Архитектура и качество кода

1. **Тройное дублирование кода** между `SmartVideoCutter` (WinForms), `SmartVideoCutterVLC` и `SmartVideoCutterFlyleaf`:
   `FFmpegService`, `YoloFaceDetector`, `Keyframe`, `Settings`, `SettingsManager`, ProgressDialog — практически
   идентичны. **Вынести в общий класс `SmartVideoCutter.Core`** (Models + FFmpeg + Vision + Settings), который ссылают
   все три приложения.
2. **MVVM нарушается**: `MainViewModel` сам создаёт `new SettingsWindow()` / `new ProgressDialog()` (зависимость
   View→View из ViewModel). Ввести `IDialogService`.
3. **Нет DI** — сервисы создаются в конструкторах вручную. Добавить `Microsoft.Extensions.DependencyInjection` (App →
   контейнер → MainViewModel).
4. **`Settings`** реализует `INotifyPropertyChanged` вручную, хотя CommunityToolkit.Mvvm уже в проекте — заменить на
   `ObservableObject` + `[ObservableProperty]`.
5. **`settings.json` в `AppContext.BaseDirectory`** — для опубликованного приложения папка может быть read-only.
   Перенести в `%LOCALAPPDATA%\SmartVideoCutter\settings.json`.
6. **Сохранение настроек не применяется**: после смены `FfmpegPath`/`YoloPath` нужно пересоздать движок/сессию (или хотя
   бы требовать перезапуск с явным сообщением).
7. **Нет глобального обработчика исключений** (`DispatcherUnhandledException`) и нет отображения ошибок плеера: Flyleaf
   даёт `Player.OpenCompleted`/`BufferingCompleted`/`LastError`/`Status` — вывести в статус-бар.
8. **Мёртвый код**: `FFmpegService.GetVideoInfo` (не вызывается — FPS/размер уже даёт `Player.Video`), `VideoInfo` в
   Flyleaf-проекте, пустые `Viewbox` в `DetachedContent`, unused-усинги в `App.xaml.cs` (`System.Configuration`,
   `System.Data`), `GlobalUsings.cs` лежит в папке `Services`, мёртвые ветки `TensorrtExecutionProvider`/
   `CUDAExecutionProvider` (пакеты не подключены — сработает только DML/CPU).
9. **Неиспользуемый пакет `MediaInfo.Wrapper.Core`** в csproj — убрать (OpenCvSharp оставить: понадобится для пайплайна
   детекции).
10. **Нейминг/структура**: `MainWindow` в корне пространства имён, но лежит в `Views/` (остальные окна —
    `SmartVideoCutterFlyleaf.Views`); `Initialize(Dispatcher dispatcher)` принимает диспетчер, но не использует его.
11. **Документация устарела**: `Docs/Architecture.md` описывает **LibVLC** («LibVLC TakeSnapshot ()») — это копия из
    VLC-проекта. Обновить под Flyleaf. `ToDo.md` предлагает «перевести UI на Avalonia» — но FlyleafLib поддерживает
    только WPF/WinUI/WinForms, для Flyleaf-версии это противоречие; зафиксировать решение (WPF остаётся).

## 5. Производительность

1. **Seek на каждый пиксель ползунка**: `OnPositionChanged` → `SetPosition` → `CurTime = ...` при `SeekAccurate=true` —
   каждый пиксель запускает точный seek (дорого, дёргается). Решение: точный seek только по `DragCompleted` (как уже
   задумано `BeginSeek/EndSeek`), во время перетаскивания — обновлять лишь UI/превью.
2. **DispatcherTimer на 500 мс избыточен**: при `UIRefresh=true` (уже включено) Flyleaf сам шлёт `CurTime` на UI-поток
   (в примере — `UIRefreshInterval=100`). Подписаться на `Player.PropertyChanged` и убрать таймер.
3. **Готовые `Player.Commands`** (ICommands для WPF MVVM: `TogglePlayPause`, `SeekBackward/Forward`, `VolumeUp/Down`,
   `ToggleMute`, `TakeSnapshot`, `FullScreen`…) — часть кнопок можно не писать самому.
4. **ONNX**: добавить `GraphOptimizationLevel.ORT_ENABLE_ALL`, задать `IntraOp/InterOpNumThreads`; для DML — `DeviceId`.
5. **Буферы**: портить `TensorConverter` с переиспользуемыми массивами (уже есть в WinForms-проекте).
6. **`LogLevel.Debug`** в `EngineConfig` в release — шумно; сделать условным.

## 6. UI/UX

1. **Форматирование времени**: DataGrid показывает сырые мс («15000») — нужен `mm:ss.fff` (IValueConverter); то же для
   подписи позиции/длительности под слайдером (сейчас вообще нет текста времени).
2. **Enable/Disable команд**: Analyze/Export активны только при открытом файле (`CanExecute`), Play/Pause — по
   `Player.Status` (иконка меняется).
3. **Drag & Drop** видеофайла на окно (FlyleafHost/FlyleafME это поддерживают: `OpenOnDrop`).
4. **Горячие клавиши**: в меню заявлено `Ctrl+O` (`InputGestureText`), но `KeyBinding` не определён; добавить
   Space/стрелки (у Flyleaf есть встроенные `Config.Player.KeyBindings`).
5. **Статус-бар** (ошибки, прогресс анализа/экспорта, FPS/разрешение из `Player.Video`).
6. **ProgressDialog без кнопки «Отмена»** — добавить `CancellationToken`.
7. **Локализация**: интерфейс смешанный (меню по-русски, кнопки «Play/Pause», «Analyze», «Export», заголовки колонок
   по-английски) — унифицировать.
8. **Опционально**: заменить самописный плеер-UI на готовый контрол **`FlyleafME`** (FlyleafLib.Controls.WPF уже
   подключён!) — полный плеер с баром, слайдером, popup-меню и настройками, тема Material Design. Это сильно сократит
   XAML и даст «коробочный» UX; кастомный overlay для рамок лиц при этом сохраняется.
9. Заголовок окна с именем файла; «О программе» без команды.

## 7. Инфраструктура проекта

1. **Тесты**: для Flyleaf-проекта тестового проекта нет (есть только заглушка `SmartVideoCutterVLC.Tests/UnitTest1.cs`).
   Создать `SmartVideoCutterFlyleaf.Tests`: парсинг JSON ключевых кадров (фикстуры), `YoloResultParser`
   (тензоры-фикстуры), `NonMaxSuppression`, `TensorConverter`, `SettingsManager` (temp-папка).
2. **CI**: GitHub Actions (windows-latest): `dotnet build` + `dotnet test`.
3. **Публикация**: `RuntimeIdentifier=win-x64` вынесенный в csproj принуждает к RID-build в dev; перенести в
   publish-профиль; добавить single-file publish.
4. **README.md** в корне (запуск, где взять FFmpeg-папку и ONNX-модели, архитектура) — сейчас его нет.
5. **`.gitignore`**: добавить `*.user` (в репо лежит `SmartVideoCutter.sln.DotSettings.user`), `publish/`.
6. **csproj**: убрать избыточные `<Page Update=...>` блоки (SDK генерирует сам).

## 8. Приоритетная дорожная карта

**Этап 1 — стабильность (баги):**

1. Исправить единицы `SeekToTimestamp` (мс → тики/`Seek(ms)`).
2. Null-безопасность: `_filePath` → `string?`, `CanExecute` у Analyze/Export, `KeyframeList = new()`.
3. `Keyframe` → `ObservableObject` (чекбоксы работают).
4. Обработка ошибок ffprobe + валидация `FfmpegPath` (с понятным сообщением).
5. Подписка на `Player.PropertyChanged` (`CurTime`, `Duration`, `Status`, `LastError`) вместо таймера; убрать
   `SeekAccurate`-спам во время drag; `Engine.Stop()` при выходе; глобальный обработчик исключений + статус-бар.

**Этап 2 — ядро продукта:**

6. Порт пайплайна детекции лиц (TensorConverter, YoloResultParser, NMS, `Detect`) + захват через
   `TakeSnapshotToBitmapSource` + overlay-рамки в `FlyleafHost`.
7. Порт экспорта (`VideoProcessor`/`FfmpegEncoder`/`VideoExporterService` + `VideoSegment`) + UI выбора сегментов,
   `SaveFileDialog`, прогресс с отменой.

**Этап 3 — «умность» и качество:**

8. ArcFace-распознавание + анализ сегментов с человеком (из WinForms-проекта).
9. Общий `SmartVideoCutter.Core` (убрать дублирование между тремя проектами).
10. DI, `IDialogService`, `%LOCALAPPDATA%` для настроек, применение настроек без перезапуска.

**Этап 4 — инфраструктура и UX:**

11. Тестовый проект + CI.
12. UX-пакет: форматирование времени, DnD, хоткеи, статус-бар, локализация, (опц.) `FlyleafME`.
13. README, обновление `Architecture.md` (убрать LibVLC-описание), publish-профиль.
