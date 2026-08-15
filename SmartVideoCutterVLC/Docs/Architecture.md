## Логическая архитектура SmartVideoCutter

### 📦 Основные компоненты

```
┌─────────────────────────────────────────────────────────────────┐
│                    MainWindow (WPF View)                        │
│  Показывает видео, полосу прогресса, список кадров, кнопки      │
└──────────────────────────┬──────────────────────────────────────┘
                           │ привязка (DataBinding)
┌──────────────────────────▼──────────────────────────────────────┐
│                  MainViewModel                                   │
│  Координатор: получает команды от UI, управляет сервисами        │
│  Команды: OpenFile, PlayPause, AnalyzeFile, ExportFile,         │
│           SeekToTimestamp, OpenSettings                          │
└───┬──────────────┬───────────────┬──────────────────────────────┘
    │              │               │
    ▼              ▼               ▼
┌──────────┐ ┌──────────────┐ ┌──────────────────┐
│ MediaPlayer│ │  FFmpeg      │ │  YoloFace        │
│ Service    │ │  Service     │ │  Detector        │
│ (LibVLC)  │ │ (ffprobe)    │ │ (ONNX Runtime)   │
└──────────┘ └──────────────┘ └──────────────────┘
```

---

### 🔄 Последовательность работы

#### 1. Запуск приложения

```
App.xaml → MainWindow → MainViewModel
                ↓
        Инициализация MediaPlayerService (LibVLC)
        Создание YoloFaceDetector (загрузка ONNX модели)
        Запуск таймера обновления позиции (500мс)
```

#### 2. Открытие файла видео (OpenFile)

```
Пользователь нажимает "Открыть файл"
    ↓
Диалог выбора файла → путь к видео
    ↓
MediaPlayerService.LoadMedia(path)
    ↓
  ├─ FFmpegService.GetVideoInfo() → получить ширину, высоту, FPS
  ├─ Создание RAM-буфера для кадров
  └─ Обновление MaxPosition (длительность видео)
```

#### 3. Воспроизведение видео (PlayPause)

```
Пользователь нажимает Play/Pause
    ↓
LibVLC воспроизводит видео
    ↓
Таймер (500мс) обновляет Position
    ↓
Событие FrameReady → TryDetectFaces()
```

#### 4. Детекция лиц (TryDetectFaces)

```
Каждые N кадров (интервал = FPS, ~1 раз в секунду):
    ↓
  ├─ Проверка: не время для детекции? → пропуск
  │
  └─ Получение кадра: MediaPlayerService.GetCurrentFrame()
      │
      ├─ LibVLC TakeSnapshot() → сохранение в RAM-файл
      ├─ Чтение из RAM → байты изображения
      └─ Cv2.ImDecode() → OpenCvSharp Mat
      │
      ↓  (в фоновом потоке Task.Run)
      │
      YoloFaceDetector.Detect(frame)
      │
      ├─ Preprocess: Letterbox resize → BGR→RGB → нормализация → тензор [1,3,640,640]
      ├─ ONNX Inference: прогон через модель YOLO
      └─ Postprocess: парсинг выхода → фильтрация по confidence → NMS → Rect[]
      │
      ↓
      Возврат в UI поток через Dispatcher
      │
      ↓
      Событие FacesDetected → MainWindow рисует рамки на видео
```

#### 5. Анализ видео (AnalyzeFile)

```
Пользователь нажимает "Анализировать"
    ↓
Показать ProgressDialog
    ↓
  (в фоновом потоке)
  │
  FFmpegService.GetVideoKeyframes(path)
  │
  ├─ Запуск ffprobe с запросом packet=pts_time,flags
  ├─ Парсинг JSON вывода
  └─ Фильтрация: только пакеты с флагом 'K' (keyframe)
  │
  ↓
  Получен список Keyframe с временными метками
  │
  ↓
  Обновление KeyframeList в UI (список ключевых кадров)
  Закрытие ProgressDialog
```

#### 6. Перемотка к ключевому кадру (SeekToTimestamp)

```
Пользователь выбирает ключевой кадр из списка
    ↓
MainViewModel.SeekToTimestamp(keyframe)
    ↓
MediaPlayerService.SetPosition(keyframe.Timestamp * 1000)
    ↓
LibVLC перемотка к указанному времени (мс)
```

#### 7. Настройки (OpenSettings)

```
Пользователь нажимает "Настройки"
    ↓
Открытие SettingsWindow
    ↓
Редактирование путей: ArcFace, YOLO, FFmpeg
    ↓
SettingsManager.Save() → сохранение в settings.json
```

---

### 🧩 Паттерны архитектуры

| Паттерн         | Где используется                                                                  |
|-----------------|-----------------------------------------------------------------------------------|
| **MVVM**        | View ↔ ViewModel ↔ Model, команды вместо обработчиков                             |
| **Service**     | FFmpegService, MediaPlayerService, YoloFaceDetector — инкапсуляция внешней логики |
| **Observer**    | INotifyPropertyChanged, события FrameReady, FacesDetected                         |
| **Disposable**  | Корректное освобождение ресурсов (LibVLC, ONNX, OpenCV, таймеры)                  |
| **Async/Await** | Фоновая детекция лиц, анализ видео                                                |
| **Lock**        | Thread-safe счётчик кадров в TryDetectFaces                                       |

---

### 📊 Поток данных

```
Видео файл
    ↓
LibVLC (декодирование стрима) → Отображение в UI
    ↓
Снимки кадров → RAM буфер → OpenCV Mat
    ↓
ONNX модель (YOLO) → Тензор вывода
    ↓
NMS фильтрация → Список лиц (Rect)
    ↓
Отрисовка рамок на видео в UI
```

Теперь clearer? Если нужно подробнее по какому-то этапу — скажите!