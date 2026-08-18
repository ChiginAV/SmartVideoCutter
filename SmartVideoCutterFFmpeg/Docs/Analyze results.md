# Анализ проекта SmartVideoCutterFlyleaf

## 1. Критические баги (фиксить в первую очередь)

| # | Баг                                                                                                                                                               | Где                                         |
|---|-------------------------------------------------------------------------------------------------------------------------------------------------------------------|---------------------------------------------|
| 1 | **NullReferenceException в `AnalyzeFile`**: `_filePath` не инициализирован (`private string _filePath;` при `Nullable=enable`) и не проверяется.                  | `MainViewModel.cs:16,128`                   |
| 2 | `Keyframe` не реализует `INotifyPropertyChanged` → чекбокс `IsSelected` в DataGrid не обновляется                                                                 | `MainViewModel.cs:26`, `Models/Keyframe.cs` |
| 3 | **Нет обработки ошибок ffprobe**: пустой/неверный `FfmpegPath` → падение `Process.Start` без сообщения; нет проверки `ExitCode`, таймаута, чтения `StandardError` | `FFmpegService.cs`                          |
| 4 | Деление на ноль при `r_frame_rate = "0/0"` (VFR)                                                                                                                  | `FFmpegService.cs:42`                       |
| 5 | `VideoInfo` никогда не заполняется (вызов закомментирован в `LoadMedia`) — свойство всегда `null`                                                                 | `MediaPlayerService.cs:91`                  |
| 6 | Финализатор `~MediaPlayerService()` не освобождает неуправляемые ресурсы (плеер) — либо убрать, либо сделать корректным                                           | `MediaPlayerService.cs:190`                 |
| 7 | `OpenFile`/`AnalyzeFile` объявлены `async void` без `await`/`try-catch` — любое исключение роняет UI-поток                                                        | `MainViewModel.cs`                          |

## 2. Отсутствующая функциональность (главные «дыры»)

1. **Детекция лиц не портирована.** В WinForms-проекте полный пайплайн: `TensorConverter` (переиспользуемые буферы) →
   `YoloFaceDetector.Detect` → `YoloResultParser` (sigmoid, NCHW/NHWC) → `NonMaxSuppression`. В Flyleaf-проекте из этого
   только «голая» сессия. План порта:
    - ленивая загрузка модели (при включении модель будет грузиться при старте даже если пользователь не будет
      анализировать).
2. **Экспорт (ядро «cutter»!) пустой.** В WinForms-проекте есть `VideoProcessor` (ffprobe/ffmpeg), `FfmpegEncoder`
   (нарезка сегментов), `VideoExporterService` (temp-папка с GUID, list-файл, склейка, гарантированная очистка), модель
   `VideoSegment`. Всё это нужно портировать, плюс UI: выбор сегментов (уже есть чекбоксы в DataGrid), `SaveFileDialog`,
   прогресс с отменой (`CancellationToken`).
3. **Распознавание лиц (ArcFace)** — в настройках есть путь, кода нет. Это «умная» часть продукта (найти сегменты с
   конкретным человеком). Портировать `ArcFaceRecognizer` + `FaceSimilarityCalculator` + логику `VideoAnalyzerService` —
   как этап 2.
4. **Offline-анализ через Flyleaf Extractor** — библиотека умеет извлекать кадры с шагом N (см. пример
   FlyleafExtractor). Для анализа всего видео это быстрее и правильнее, чем покадровые снимки из плеера.

## 3. Архитектура и качество кода

1. **MVVM нарушается**: `MainViewModel` сам создаёт `new SettingsWindow()` / `new ProgressDialog()` (зависимость
   View→View из ViewModel). Ввести `IDialogService`.
2. **Нет глобального обработчика исключений** (`DispatcherUnhandledException`) и нет отображения ошибок плеера: Flyleaf
   даёт `Player.OpenCompleted`/`BufferingCompleted`/`LastError`/`Status` — вывести в статус-бар.
3. **Мёртвый код**: `FFmpegService.GetVideoInfo` (не вызывается — FPS/размер уже даёт `Player.Video`), `VideoInfo` в
   Flyleaf-проекте, пустые `Viewbox` в `DetachedContent`, unused-усинги в `App.xaml.cs` (`System.Configuration`,
   `System.Data`), `GlobalUsings.cs` лежит в папке `Services`, мёртвые ветки `TensorrtExecutionProvider`/
   `CUDAExecutionProvider` (пакеты не подключены — сработает только DML/CPU).
4. **Нейминг/структура**: `Initialize(Dispatcher dispatcher)` принимает диспетчер, но не использует его.

## 4. Производительность

1. **ONNX**: добавить `GraphOptimizationLevel.ORT_ENABLE_ALL`, задать `IntraOp/InterOpNumThreads`; для DML — `DeviceId`.
2. **Буферы**: портировать `TensorConverter` с переиспользуемыми массивами (уже есть в WinForms-проекте).

## 5. UI/UX

1. **Drag & Drop** видеофайла на окно (FlyleafHost/FlyleafME это поддерживают: `OpenOnDrop`).
2. **Горячие клавиши**: в меню заявлено `Ctrl+O` (`InputGestureText`), но `KeyBinding` не определён; добавить
   Space/стрелки (у Flyleaf есть встроенные `Config.Player.KeyBindings`).
3. **ProgressDialog без кнопки «Отмена»** — добавить `CancellationToken`.
4. **Локализация**: интерфейс смешанный (меню по-русски, кнопки «Play/Pause», «Analyze», «Export», заголовки колонок
   по-английски) — унифицировать.

## 6. Инфраструктура проекта

1. **Тесты**: для Flyleaf-проекта тестового проекта нет (есть только заглушка `SmartVideoCutterVLC.Tests/UnitTest1.cs`).
   Создать `SmartVideoCutterFlyleaf.Tests`: парсинг JSON ключевых кадров (фикстуры), `YoloResultParser`
   (тензоры-фикстуры), `NonMaxSuppression`, `TensorConverter`, `SettingsManager` (temp-папка).
2. **Публикация**: `RuntimeIdentifier=win-x64` вынесенный в csproj принуждает к RID-build в dev; перенести в
   publish-профиль; добавить single-file publish.
3. **csproj**: убрать избыточные `<Page Update=...>` блоки (SDK генерирует сам).

## 7. Приоритетная дорожная карта

**Этап 1 — стабильность (баги):**

1. Null-безопасность: `_filePath` → `string?`, `CanExecute` у Analyze/Export, `KeyframeList = new()`.
2. `Keyframe` → `ObservableObject` (чекбоксы работают).
3. Обработка ошибок ffprobe + валидация `FfmpegPath` (с понятным сообщением).

**Этап 2 — ядро продукта:**

4. Порт пайплайна детекции лиц (TensorConverter, YoloResultParser, NMS, `Detect`) + захват через
   `TakeSnapshotToBitmapSource` + overlay-рамки в `FlyleafHost`.
5. Порт экспорта (`VideoProcessor`/`FfmpegEncoder`/`VideoExporterService` + `VideoSegment`) + UI выбора сегментов,
   `SaveFileDialog`, прогресс с отменой.

**Этап 3 — «умность» и качество:**

6. ArcFace-распознавание + анализ сегментов с человеком (из WinForms-проекта).

**Этап 4 — инфраструктура и UX:**

7. Тестовый проект.
8. UX-пакет: DnD, хоткеи, локализация.
9. Publish-профиль.
