# ToDo

## Резюме сессии 2026-08-21 (восстановление контекста)

Сделано: внутрипроцессный декодер FFmpeg через FFmpeg.AutoGen 7.1.1 вместо запуска ffmpeg.exe на отрезок.

- `Services/Video/InProcessDecoder.cs` — unsafe-декодер: один AVFormatContext переиспользуется, seek = av_seek_frame (~
  мс vs ~400 мс у CLI). CUDA через hw_device_ctx (эквивалент cuvid), кадры → sws_scale → BGR24 Mat. ВАЖНО: кадры с
  tRel < 0 (между ключевым кадром и startMs) декодируются, но НЕ отдаются в анализ и не считаются в n — иначе человек из
  предыдущего отрезка делает все отрезки «валидными» (баг, найден 21.08).
- `Services/Video/FFmpegService.cs` — пул декодеров (до 2 prefetcher'а параллельно), ParseSelect разбирает
  select-выражения FaceRecognizer (eq/mod/between/gte, OR «+»), fallback на ffmpeg.exe при любой неудаче.
  SVC_FFMPEG_INPROC=0 — выключить внутрипроцессный путь.
- `ViewModels/MainViewModel.cs` — ReleaseDecoders () в finally после анализа.
- POC-замеры: cuvid 5.1–7.7x realtime; ~100 мс/отрезок vs ~400 у CLI.

ОЖИДАЕТ ПРОВЕРКИ (главное!): перепроверить анализ тестового видео после фикса tRel<0 — до фикса все отрезки были
валидными ошибочно. A/B: SVC_FFMPEG_INPROC=0 даёт эталонный exe-путь на том же видео.

## Баг «выбирается только первый отрезок» (найдено и починено 21.08)

Симптом: анализ определял человека только в самом первом отрезке, остальные — пустые. Причина:
в [InProcessDecoder.cs](../Services/Video/InProcessDecoder.cs) `seekTs` считался в `AV_TIME_BASE` (1/1e6), а
`av_seek_frame(fmt, streamIdx >= 0, ts)` ожидает timestamp в time_base **потока** (напр. 1/12800). seekTs был завышен ~в
den/tb.den раз → перемотка улетала к концу файла → все кадры отрезка имели tRel < 0 и отбрасывались. Первый отрезок
(startMs = 0 → seekTs = 0) работал корректно — отсюда симптом. Фикс:
`seekTs = (long)(startMs / 1000.0 * tb.den / tb.num)`.

Для визуальной проверки кадров, идущих на инференс: `SVC_DEBUG_FRAMES=<папка>` — внутрипроцессный декодер дампает каждый
отобранный кадр PNG с именем `seg_<start>_<end>_n<N>_t<T>.png`.

## Экспорт: рассинхрон A/V и окно прогресса (21.08)

- Рассинхрон/тормоза в итоговом видео: `-c copy` отбрасывал аудио-пакеты до точки нарезки → звук начинался на ~23 мс
  (длина AAC-кадра) позже видео на каждой границе сегмента. Фикс в
  [FFmpegService.CopyRange](../Services/Video/FFmpegService.cs): видео — `-c:v copy` (границы — ключевые кадры), аудио —
  перекодирование (aac / libmp3lame для .avi) + `-avoid_negative_ts make_zero`. Перекодирование хотя бы одного потока
  включает у ffmpeg accurate_seek → точная нарезка аудио.
- Окно прогресса: строка Details под сообщением
  ([ProgressDialogViewModel.Details](../ViewModels/ProgressDialogViewModel.cs),
  [ProgressDialog.xaml](../Views/ProgressDialog.xaml)). Анализ: «Алгоритм: быстрый/точный · декод: GPU (CUDA,
  внутрипроцессный) / CPU ...» через [FFmpegService.DescribeDecoding](../Services/Video/FFmpegService.cs); экспорт:
  «Видео: без перекодирования · Аудио: перекодирование (AAC/MP3)».

## Анализ медленный + донастройка экспорта (21.08, 2-я итерация)

- Скорость анализа: [InProcessDecoder.SeekAndReadFrames](../Services/Video/InProcessDecoder.cs) декодировал ВЕСЬ отрезок
  в List<Mat> до первого yield → ранний выход (человек найден) не экономил время, prefetcher блокировался полным
  декодом. Фикс: стриминг через BlockingCollection (8) + Task.Run (DecodeSegment) + linkedCts — первый кадр сразу после
  GOP-заголовка, Dispose итератора реально останавливает декод.
- Экспорт (если рассинхрон остался): добавлен `-shortest` в CopyRange — длина copy-видео и перекодированного аудио
  теперь совпадает точно (без него расхождение ~50 мс/сегмент накапливалось на склейках concat).
- Диагностика: SVC_DEBUG_TIMING=1 — самый медленный отрезок пишется в bench.log ([TIMING] строка).

Технический нюанс: инструменты редактирования AI (apply_diff/write_to_file) глючат на файлах ~300+ строк (применяют
застарелые версии, дублируют хвосты). Workaround: генерировать полный файл во временный (tools/*.cs), копировать
вручную; проверять состояние через PowerShell ReadAllText.

## P1. Оптимизация производительности

- [x] Профилировщик: bottleneck анализа — старт ffmpeg.exe + seek ~400 мс/отрезок (инференс и декод не в боте).
- [x] exe → dll FFmpeg: да, эффект есть. Внедрён внутрипроцессный декодер через FFmpeg.AutoGen
  ([InProcessDecoder.cs](../Services/Video/InProcessDecoder.cs)): один AVFormatContext на видео переиспользуется между
  отрезками (пул в [FFmpegService.cs](../Services/Video/FFmpegService.cs)), seek = av_seek_frame ~мс вместо
  ~400 мс «процесс + -ss». Fallback на ffmpeg.exe при любой неудаче; SVC_FFMPEG_INPROC=0 — выключить.
- [x] GPU: аппаратный декод включён в обоих путях (cuvid / hw_device_ctx); проверено 7.7x realtime. Дальнейший GPU
  (инференс) — отдельная задача (см. ниже).
- Изучить, можно ли какой-то мой код вынести на обработку в GPU (инференс ONNX → DirectML уже есть; дальше — только
  CUDA-модели/ONNX Runtime GPU, эффект ~в 2 раза на инференсе при том же декоде).

## P2. Интерфейс

- Сделать статус бар двухуровневым:
    - Один уровень для статуса воспроизведения и отображения имени открытого файла.
    - Другой для отображения ошибок и прочих сообщений.

## Команды для починки готовых файлов:

- mkvmerge -o fixed_output.mkv broken_input.mkv
- ffmpeg -y -fflags +genpts+igndts -i broken_input.mp4 -c copy -avoid_negative_ts make_zero -movflags +faststart
  fixed_output.mp4
