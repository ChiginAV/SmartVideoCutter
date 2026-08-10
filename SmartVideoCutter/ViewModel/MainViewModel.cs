using System.ComponentModel;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using SmartVideoCutter.Models;
using SmartVideoCutter.Services;

namespace SmartVideoCutter.ViewModels
{
    /// <summary>
    /// ViewModel для экрана резки видео.
    /// Только UI-состояние — бизнес-логика делегируется в VideoSessionService и VideoExporterService.
    /// </summary>
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly VideoSessionService _session;
        private readonly VideoExporterService _exporter;

        // --- Состояние видео ---
        private string? _videoPath;
        private int _totalFrames;
        private (int W, int H)? _frameSize;
        private Mat _currentFrame = new Mat();
        private List<Rect> _detectedPeople = new List<Rect>();
        private Bitmap? _currentFrameImage;
        private int _timelinePosition;

        // --- Состояние анализа ---
        private float[]? _referenceVector;
        private bool _personSelected;
        private List<VideoSegment>? _analyzedSegments;

        // --- Состояние UI ---
        private string _statusText = "Откройте видео для начала работы.";
        private bool _canAnalyze;
        private bool _canExport;
        private bool _isBusy;

        public MainViewModel(VideoSessionService session, VideoExporterService exporter)
        {
            _session = session;
            _exporter = exporter;
        }

        // === СВОЙСТВА (для Data Binding) ===

        public string? VideoPath
        {
            get => _videoPath;
            set { _videoPath = value; OnPropertyChanged(); }
        }

        public int TotalFrames
        {
            get => _totalFrames;
            private set { _totalFrames = value; OnPropertyChanged(); }
        }

        public Bitmap? CurrentFrameImage
        {
            get => _currentFrameImage;
            private set { _currentFrameImage = value; OnPropertyChanged(); }
        }

        public int TimelinePosition
        {
            get => _timelinePosition;
            set { _timelinePosition = value; OnPropertyChanged(); }
        }

        public List<Rect> DetectedPeople => _detectedPeople;

        public bool PersonSelected
        {
            get => _personSelected;
            private set { _personSelected = value; OnPropertyChanged(); }
        }

        public List<VideoSegment>? AnalyzedSegments
        {
            get => _analyzedSegments;
            private set { _analyzedSegments = value; OnPropertyChanged(); }
        }

        public string StatusText
        {
            get => _statusText;
            private set { _statusText = value; OnPropertyChanged(); }
        }

        public bool CanAnalyze
        {
            get => _canAnalyze;
            private set { _canAnalyze = value; OnPropertyChanged(); }
        }

        public bool CanExport
        {
            get => _canExport;
            private set { _canExport = value; OnPropertyChanged(); }
        }

        public bool IsBusy
        {
            get => _isBusy;
            private set { _isBusy = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Размер оригинального кадра (кэшируется при открытии видео).
        /// </summary>
        public (int W, int H)? FrameSize
        {
            get => _frameSize;
            private set { _frameSize = value; OnPropertyChanged(); }
        }

        // === МЕТОДЫ (делегирование в сервисы) ===

        public void OpenVideo(string path)
        {
            VideoPath = path;

            var info = _session.OpenVideo(path);
            TotalFrames = info.TotalFrames;
            _frameSize = (info.Width, info.Height);
            OnPropertyChanged(nameof(FrameSize));

            TimelinePosition = 0;
            LoadFrame(0);

            CanAnalyze = false;
            CanExport = false;
            PersonSelected = false;
            AnalyzedSegments = null;

            StatusText = "Двигайте ползунок, чтобы найти нужный кадр, и кликните по человеку!";
        }

        public void LoadFrame(int frameIndex)
        {
            if (string.IsNullOrEmpty(VideoPath)) return;

            // Синхронизируем позицию таймлайна ДО загрузки кадра
            TimelinePosition = Math.Clamp(frameIndex, 0, Math.Max(0, TotalFrames - 1));

            _currentFrame?.Dispose();

            var result = _session.GetFrameWithDetection(VideoPath, frameIndex);
            _currentFrame = result.Frame;
            _detectedPeople = result.People;
            CurrentFrameImage = result.Image;

            if (_detectedPeople.Count > 0)
            {
                var r = _detectedPeople[0];
                StatusText = $"Лиц: {_detectedPeople.Count}. Первое: X={r.X}, Y={r.Y}, W={r.Width}, H={r.Height} | Кадр: {_currentFrame.Width}x{_currentFrame.Height}";
            }
            else
            {
                StatusText = "Лиц не найдено.";
            }

            CanAnalyze = _personSelected;
        }

        public void SelectPersonAt(int clickX, int clickY)
        {
            if (_currentFrame.Empty() || _detectedPeople.Count == 0) return;

            var selection = _session.SelectPerson(_currentFrame, _detectedPeople, clickX, clickY);

            CurrentFrameImage = BitmapConverter.ToBitmap(selection.VisualFrame);

            if (selection.Found)
            {
                _referenceVector = selection.Vector;
                PersonSelected = true;
                CanAnalyze = true;
                StatusText = "Человек выбран! Теперь нажмите 'Анализировать'.";
            }
            else
            {
                StatusText = $"Промах! Клик в точку ({clickX}, {clickY}). Попробуйте попасть в зелёную рамку.";
            }
        }

        public async Task StartAnalysisAsync(
            Action<int, int, int> progressCallback,
            CancellationToken cancelToken)
        {
            IsBusy = true;
            StatusText = "Анализируем видео... Пожалуйста, подождите.";

            try
            {
                AnalyzedSegments = await Task.Run(() =>
                {
                    return _session.Analyze(
                        VideoPath!,
                        _referenceVector!,
                        progressCallback,
                        cancelToken);
                }, cancelToken);

                if (AnalyzedSegments != null && AnalyzedSegments.Count > 0)
                {
                    StatusText = $"Найдено {AnalyzedSegments.Count} сегмент(ов)! Теперь можно вырезать видео.";
                    CanExport = true;
                }
                else
                {
                    StatusText = "Человек не найден в видео. Попробуйте другой кадр-эталон.";
                }
            }
            catch (OperationCanceledException)
            {
                StatusText = "Анализ отменён.";
            }
            catch (Exception ex)
            {
                StatusText = $"Ошибка: {ex.Message}";
                throw;
            }
            finally
            {
                IsBusy = false;
                CanAnalyze = _personSelected;
            }
        }

        public async Task ExportAsync(
            string outputFileName,
            Action<int, int, int> progressCallback,
            CancellationToken cancelToken)
        {
            IsBusy = true;
            StatusText = "Нарезка и склейка видео... Пожалуйста, подождите.";

            try
            {
                if (AnalyzedSegments == null || AnalyzedSegments.Count == 0)
                {
                    StatusText = "Сегменты не найдены. Сначала нажмите 'Анализировать'.";
                    return;
                }

                double fps = _session.GetFPS(VideoPath!);

                await _exporter.ExportAsync(
                    VideoPath!,
                    outputFileName,
                    AnalyzedSegments,
                    fps,
                    progressCallback,
                    cancelToken);

                StatusText = $"Готово! {AnalyzedSegments.Count} сегмент(ов), файл \"{Path.GetFileName(outputFileName)}\" сохранён.";
            }
            catch (OperationCanceledException)
            {
                StatusText = "Экспорт отменён.";
            }
            catch (Exception ex)
            {
                StatusText = $"Ошибка при экспорте: {ex.Message}";
                throw;
            }
            finally
            {
                IsBusy = false;
                CanExport = AnalyzedSegments != null && AnalyzedSegments.Count > 0;
            }
        }

        public void Dispose()
        {
            _currentFrame?.Dispose();
            _currentFrameImage?.Dispose();
        }

        // === INotifyPropertyChanged ===

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}