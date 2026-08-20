# Архитектура SmartVideoCutterFFmpeg

## Пользовательский сценарий

- Пользователь открывает видео
- Ищет подходящий кадр, где присутствует лицо нужного человека
- Каждый раз на паузе запускается детекция лиц (FaceAiSharp) и рисуются рамки
- Пользователь кликает на рамку нужного человека и нажимает "Анализировать"
- Лицо распознается и происходит его поиск по всему видео.
- Программа делит видео на отрезки по ключевым кадрам и, если выбранный человек хотя бы раз появляется в каком-то
  отрезке, то он считается валидным.
- После анализа программа выводит список ключевых кадров, где флагами отмечены кадры, с которых начинаются валидные
  отрезки
- Пользователь может проверить результат анализа. Двойной клик по временной метке кадра перематывает видео на этот
  момент. Пользователь просматривает отрезок и убеждается, что выбранный человек присутствует/отсутствует. Если
  пользователь обнаруживает неточность анализа, он может вручную проставить/снять флаги на нужных ключевых кадрах.
- Когда пользователя все устраивает, он нажимает "Экспорт".
- Программа склеивает выбранные отрезки по ключевым кадрам и сохраняет итоговое видео без кодирования в оригинальном
  качестве.

## Стек

- WPF, `net10.0-windows`, x64, C# nullable
- CommunityToolkit.Mvvm 8.4.2 (`[ObservableProperty]`, `[RelayCommand]` — source generators)
- FaceAiSharp (детекция/распознавание лиц, ONNX Runtime DirectML)
- FFMediaToolkit (декодирование кадров для воспроизведения)
- FFmpeg/ffprobe (внешние бинарники, путь задаётся в настройках; ключевые кадры, экспорт `-c copy`)
- OpenCvSharp4 (образы кадров)

## Слои

```
Views (XAML + code-behind)
  ↓ биндинги, команды
ViewModels (ObservableObject, без System.Windows)
  ↓ вызовы
Services (домен + WPF-сервисы)
  ↓
Models (POCO + ObservableObject для элементов списков)
```

### Views

| Файл | Назначение |
|---|---|
| `MainWindow.xaml(.cs)` | Главное окно. Composition root: создаёт `MainViewModel(new DialogService())`, на `Closed` — `Dispose` VM |
| `VideoPlayerControl.xaml(.cs)` | Контрол плеера: видео, слайдер, оверлей рамок лиц (ItemsControl + DataTemplate). Code-behind — только мышевые жесты оверлея/слайдера, логика — команды `PlayerViewModel` |
| `ProgressDialog.xaml(.cs)` | Тонкое окно прогресса, биндится к `ProgressDialogViewModel` |
| `SettingsWindow.xaml(.cs)` | Окно настроек. Composition root: `new SettingsViewModel(new DialogService())` |

Правило: в ViewModels **нет** `new *Window`/`new *Dialog`/`Dispatcher` — View создаются только в
`DialogService` и в code-behind окон.

### ViewModels

| VM | Назначение |
|---|---|
| `MainViewModel` | Оркестрация сценария: OpenFile, AnalyzeFile, ExportFile, OpenSettings, статус. Владеет `MediaPlayerService`, `FaceDetector`, `FaceRecognizer`, `DialogService` и дочерней `PlayerViewModel` |
| `PlayerViewModel` | Плеер и лица в текущем кадре: воспроизведение, перемотка, IsDragging слайдера, детекция на паузе, выбор лица (SelectedFaceIndex), VideoFrame |
| `ProgressDialogViewModel` | Состояние прогресса (Message, ProgressValue, IsIndeterminate), отмена (Cts, IsCancelled), CloseRequested. Единственный VM, владеющий `Dispatcher` — маршализация отчётов прогресса в UI |
| `SettingsViewModel` | Форма настроек (путь FFmpeg, алгоритм анализа, тема), валидация, сохранение через `SettingsManager` |

Дочерняя связь: `MainViewModel.Player` — child-VM; `MainWindow.xaml` биндит `VideoPlayerControl`
на `DataContext="{Binding Player}"`. События `PlayerViewModel` (`FaceSelectionChanged`,
`StatusMessageChanged`) подписаны в `MainViewModel`.

### Services

| Сервис | Тип | Назначение |
|---|---|---|
| `MediaPlayerService` | домен (WPF-зависимый) | Декодирование (FFMediaToolkit) на фоновом потоке, статус, Seek. **Владеет `Dispatcher`** (захватывается в `Initialize()`): все события/PropertyChanged поднимаются в UI через `RaiseUi`/`ApplyFrameOnUi` |
| `FFmpegService` | домен, static | ffprobe-ключевые кадры, извлечение кадров, `BuildSegments`, `ExportSegments` (склеивание `-c copy`), `GetUniqueFileName` |
| `FaceDetector` | домен | FaceAiSharp-детекция лиц на кадре |
| `FaceRecognizer` | домен | Embedding референсного лица, анализ отрезков (алгоритмы из настроек), прогресс-колбэк + CancellationToken |
| `DialogService` | WPF | ОС-диалоги (OpenFileDialog/SaveFileDialog/OpenFolder), `ShowProgress(vm, modal)`, `ShowSettings()`. **Единственное место создания View-окон** (ProgressDialog, SettingsWindow). Stateless — создаётся там, где создаётся VM |
| `SettingsManager` | домен, static | Загрузка/сохранение JSON-настроек, `CurrentSettings` (ObservableObject) |
| `ThemeHelper` | WPF | Привязка заголовка окна к теме (Win32 DWM-атрибуты) |

### Models

- `Settings` (ObservableObject) — путь FFmpeg, алгоритм анализа, тема
- `VideoInfo` — метаданные видео (width/height/fps)
- `Keyframe` (ObservableObject) — временная метка, `IsSelected` (флаг валидности), `HasPerson`
- `FaceBox` — рамка лица в координатах кадра
- `SelectedFace` — выбранное лицо: рамка + embedding
- `AppAnalysisAlgorithm`, `AppThemeMode` — enum'ы

## Composition root (DI-контейнера нет)

Проводка ручная, в code-behind окон:

```csharp
// MainWindow.xaml.cs
DataContext = new MainViewModel(new DialogService());
// SettingsWindow.xaml.cs
DataContext = new SettingsViewModel(new DialogService());
```

`MainViewModel` сам создаёт `MediaPlayerService`, `FaceDetector`, `FaceRecognizer` (try/catch —
модели могут не загрузиться). `App.OnStartup` загружает настройки (`SettingsManager.Load()`),
применяет тему и ставит глобальные обработчики исключений
(`DispatcherUnhandledException`, `TaskScheduler.UnobservedTaskException` → `StatusMessage`).

## Ключевые решения

- **Без интерфейсов** — конкретные классы; DI не нужен, тестируемость не приоритет.
- **Dispatcher только в сервисах/прогресс-VM**: `MediaPlayerService` маршализует все события в UI
  (`RaiseUi`), `ProgressDialogViewModel` — отчёты прогресса (`PostToUi`). ViewModels не знают о потоках.
- **Модальный прогресс** (AnalyzeFile/ExportFile): работа (`Task.Run`) стартует **до** `ShowProgress`;
  `task.ContinueWith(...)` закрывает окно по завершении; `finally { progress.RequestClose(); }` — страховка.
- **Отмена**: `ProgressDialog.OnClosed → Cts.Cancel()` — страховочная отмена токена. Состояние отмены
  читается только по флагу `progress.IsCancelled` (ставится командой Cancel), **не** по
  `ct.IsCancellationRequested` после `ShowDialog` — OnClosed отменяет токен даже при успехе (баг 9.9).
- **CanExecute-ограничение генератора**: `[RelayCommand(CanExecute)]` не отслеживает зависимости от
  свойств, сгенерированных `[ObservableProperty]`. Обход: частичный `OnFilePathChanged` →
  `AnalyzeFileCommand.NotifyCanExecuteChanged()`, плюс `NotifyCanExecuteChanged` по PropertyChanged
  ключевых кадров для `ExportFileCommand`.
- **Экспорт без перекодирования**: `-c copy`, границы отрезков — ключевые кадры, поэтому контейнер
  исходника сохраняется (фильтр диалога сохранения показывает его первым).
- **Темы**: `Themes/Light.xaml`/`Dark.xaml` — палитры (позиция 0 в MergedDictionaries, заменяется),
  `Themes/Styles.xaml` — общие стили; живое переключение через PropertyChanged `Settings.ThemeMode`.

## Поток данных (основной сценарий)

```
OpenFile (DialogService.OpenVideoFile)
  → MainViewModel.FilePath
  → PlayerViewModel: MediaPlayerService.LoadMedia → кадры (VideoFrame)
  → пауза → PlayerViewModel.AnalyzeFaces → FaceDetector.Detect → FaceBoxes (XAML-рамки)
  → клик по рамке → SelectedFaceIndex → SelectedFace (рамка + FaceRecognizer.GenerateReferenceEmbedding)
AnalyzeFile
  → FFmpegService.GetVideoKeyframes (ffprobe)
  → FaceRecognizer.Analyze (Task.Run, прогресс в ProgressDialogViewModel, модальное окно)
  → KeyframeList (IsSelected = валидные отрезки), панель списком
ExportFile
  → FFmpegService.BuildSegments (по выбранным ключевым кадрам)
  → DialogService.SaveVideoFile (имя = FFmpegService.GetUniqueFileName(FilePath, "_FaceCut"))
  → FFmpegService.ExportSegments (Task.Run, ffmpeg -c copy, прогресс, отмена)
```
