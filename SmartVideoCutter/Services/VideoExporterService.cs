using SmartVideoCutter.Models;

namespace SmartVideoCutter.Services
{
    /// <summary>
    /// Сервис экспорта видео: нарезка сегментов и склейка в финальный файл.
    /// Управляет временными файлами с гарантированной очисткой.
    /// </summary>
    public class VideoExporterService
    {
        private readonly VideoProcessor _videoProcessor;

        public VideoExporterService(VideoProcessor videoProcessor)
        {
            _videoProcessor = videoProcessor;
        }

        /// <summary>
        /// Экспортирует финальное видео: нарезает сегменты и склеивает их.
        /// Временные файлы создаются в отдельной папке с GUID для безопасного удаления.
        /// </summary>
        public async Task ExportAsync(
            string inputPath,
            string outputFileName,
            List<VideoSegment> segments,
            double fps,
            Action<int, int, int> progressCallback,
            CancellationToken cancelToken)
        {
            if (segments == null || segments.Count == 0)
                throw new ArgumentException("Нет сегментов для экспорта.", nameof(segments));

            // Создаём временную папку с уникальным именем
            string tempDir = Path.Combine(Path.GetTempPath(), $"SmartVideoCutter_{Guid.NewGuid()}");
            Directory.CreateDirectory(tempDir);

            var tempFiles = new List<string>();

            try
            {
                // Фаза 1: нарезка сегментов
                progressCallback?.Invoke(0, segments.Count, 0);

                for (int i = 0; i < segments.Count; i++)
                {
                    cancelToken.ThrowIfCancellationRequested();

                    var segment = segments[i];
                    string tempFile = Path.Combine(tempDir, $"segment_{i:D4}.mp4");

                    await _videoProcessor.CutSegmentAsync(
                        inputPath, tempFile, segment.StartFrame, segment.EndFrame, fps, cancelToken);

                    tempFiles.Add(tempFile);

                    // Прогресс нарезки: current, total, segmentsFound
                    progressCallback?.Invoke(i + 1, segments.Count, i + 1);
                }

                // Фаза 2: создание списка для склейки
                cancelToken.ThrowIfCancellationRequested();

                string listFile = Path.Combine(tempDir, "file_list.txt");
                var lines = tempFiles.Select(f => $"file '{f}'").ToArray();
                await File.WriteAllLinesAsync(listFile, lines, cancelToken);

                // Фаза 3: склейка
                progressCallback?.Invoke(0, 1, segments.Count);

                await _videoProcessor.JoinSegmentsAsync(listFile, outputFileName, cancelToken);

                // Сигнал — экспорт завершён
                progressCallback?.Invoke(1, 1, segments.Count);
            }
            finally
            {
                // Гарантированное удаление временных файлов
                DeleteTempFiles(tempFiles);
                DeleteDirectorySafe(tempDir);
            }
        }

        /// <summary>
        /// Безопасно удаляет временные файлы (игнорирует ошибки).
        /// </summary>
        private void DeleteTempFiles(List<string> files)
        {
            foreach (var file in files)
            {
                try
                {
                    if (File.Exists(file))
                        File.Delete(file);
                }
                catch
                {
                    // Игнорируем ошибки удаления временных файлов
                }
            }
        }

        /// <summary>
        /// Безопасно удаляет временную директорию (игнорирует ошибки).
        /// </summary>
        private void DeleteDirectorySafe(string dirPath)
        {
            try
            {
                if (Directory.Exists(dirPath))
                    Directory.Delete(dirPath, recursive: true);
            }
            catch
            {
                // Игнорируем ошибки удаления временной директории
            }
        }
    }
}