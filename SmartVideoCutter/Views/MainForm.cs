using OpenCvSharp;
using SmartVideoCutter.Services;
using SmartVideoCutter.ViewModels;

namespace SmartVideoCutter.Views
{
    /// <summary>
    /// MainForm — "тонкая" View.
    /// Вся бизнес-логика lives в MainViewModel.
    /// Форма только отображает данные и передаёт действия пользователя в ViewModel.
    /// </summary>
    public partial class MainForm : Form
    {
        private readonly MainViewModel _viewModel;

        public MainForm()
        {
            // --- Создание зависимостей (простой подход без DI-контейнера) ---
            var vision = new VisionEngine("C:/AI/Vision/yolov8n-face.onnx", "C:/AI/Vision/arcface.onnx");
            var videoProcessor = new VideoProcessor();
            var analyzer = new VideoAnalyzerService(videoProcessor, vision);
            var session = new VideoSessionService(vision, analyzer);
            var exporter = new VideoExporterService(videoProcessor);

            _viewModel = new MainViewModel(session, exporter);

            try
            {
                InitializeComponent();
                SetupBindings(); // Привязка UI к свойствам ViewModel
            }
            catch (FileNotFoundException fnfEx)
            {
                MessageBox.Show($"Файл модели не найден: {fnfEx.FileName}. Проверьте папку C:/AI/Vision/",
                    "Ошибка файла");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Произошла ошибка при запуске: {ex.Message}", "Ошибка инициализации");
            }
        }

        /// <summary>
        /// Настраивает начальную привязку UI к свойствам ViewModel.
        /// При изменении свойств ViewModel (через INotifyPropertyChanged) UI обновляется автоматически.
        /// </summary>
        private void SetupBindings()
        {
            // Привязка статуса
            lblStatus.DataBindings.Add("Text", _viewModel, "StatusText");

            // Привязка доступности кнопок
            btnAnalyze.DataBindings.Add("Enabled", _viewModel, "CanAnalyze");
            btnExport.DataBindings.Add("Enabled", _viewModel, "CanExport");

            // Подписка на изменение изображения кадра (Data Binding для Image не работает напрямую в WinForms)
            _viewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(_viewModel.CurrentFrameImage))
                {
                    InvokeIfNeeded(() =>
                    {
                        // Освобождаем старое изображение, чтобы не было "зелёных полосок" от предыдущего кадра
                        var old = picFrame.Image;
                        picFrame.Image = _viewModel.CurrentFrameImage;
                        old?.Dispose();
                    });
                }

                // Синхронизация ползунка при программном изменении TimelinePosition
                if (e.PropertyName == nameof(_viewModel.TimelinePosition))
                {
                    InvokeIfNeeded(() =>
                    {
                        if (trkTimeline.Value != _viewModel.TimelinePosition)
                            trkTimeline.Value = _viewModel.TimelinePosition;
                    });
                }

                // Обновление границ таймлайна при изменении TotalFrames
                if (e.PropertyName == nameof(_viewModel.TotalFrames))
                {
                    InvokeIfNeeded(() =>
                    {
                        trkTimeline.Minimum = 0;
                        trkTimeline.Maximum = Math.Max(1, _viewModel.TotalFrames - 1);
                    });
                }

                // Блокировка кнопок при фоновой операции
                if (e.PropertyName == nameof(_viewModel.IsBusy))
                {
                    InvokeIfNeeded(() =>
                    {
                        btnAnalyze.Enabled = _viewModel.CanAnalyze && !_viewModel.IsBusy;
                        btnExport.Enabled = _viewModel.CanExport && !_viewModel.IsBusy;
                        btnOpen.Enabled = !_viewModel.IsBusy;
                    });
                }
            };

            // Начальное состояние
            lblStatus.Text = _viewModel.StatusText;
        }

        /// <summary>
        /// Безопасный вызов делегата на UI-потоке (если нужно).
        /// </summary>
        private void InvokeIfNeeded(Action action)
        {
            if (InvokeRequired)
                Invoke(action);
            else
                action();
        }

        // === ОБРАБОТЧИКИ СОБЫТИЙ (минимальная логика) ===

        // 1. Кнопка "Открыть видео"
        private void btnOpen_Click(object sender, EventArgs e)
        {
            using OpenFileDialog dialog = new OpenFileDialog();
            dialog.Filter = "Video Files|*.avi;*.mov;*.mkv;*.mp4";

            if (dialog.ShowDialog() == DialogResult.OK)
            {
                _viewModel.OpenVideo(dialog.FileName); // <-- одна строка!
            }
        }

        // 2. Перемещение ползунка таймлайна
        private void trkTimeline_Scroll(object sender, EventArgs e)
        {
            // Синхронизация: если пользователь двигает ползунок, обновляем VM
            if (trkTimeline.Value != _viewModel.TimelinePosition)
            {
                _viewModel.LoadFrame(trkTimeline.Value);
            }
        }

        // 3. Клик по кадру: Выбор человека-эталона
        private void PicFrame_MouseClick(object sender, MouseEventArgs e)
        {
            if (_viewModel.CurrentFrameImage == null || _viewModel.DetectedPeople.Count == 0) return;

            // Пересчёт координат клика с pictureBox на реальный кадр (используем кэшированный размер)
            var frameSize = _viewModel.FrameSize;
            if (frameSize == null) return;

            int frameWidth = frameSize.Value.W;
            int frameHeight = frameSize.Value.H;

            float imageAspect = (float)frameWidth / frameHeight;
            float boxAspect = (float)picFrame.Width / picFrame.Height;
            float scale;
            float offsetX = 0, offsetY = 0;

            if (imageAspect > boxAspect)
            {
                scale = (float)picFrame.Width / frameWidth;
                offsetY = (picFrame.Height - (frameHeight * scale)) / 2f;
            }
            else
            {
                scale = (float)picFrame.Height / frameHeight;
                offsetX = (picFrame.Width - (frameWidth * scale)) / 2f;
            }

            int clickX = (int)((e.X - offsetX) / scale);
            int clickY = (int)((e.Y - offsetY) / scale);

            _viewModel.SelectPersonAt(clickX, clickY); // <-- одна строка!
        }

        /// <summary>
        /// Показывает прогресс-диалог и выполняет асинхронную операцию.
        /// Универсальный шаблон для всех фоновых задач.
        /// </summary>
        private async Task WithProgressAsync(
            string initialMessage,
            Func<ProgressDialog, CancellationToken, Task> action)
        {
            using var dialog = new ProgressDialog();
            dialog.SetIndeterminate(initialMessage);
            dialog.Show(this);

            try
            {
                await action(dialog, dialog.Cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Отмена — статус уже обновлён в ViewModel
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Внимание", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                dialog.Close();
            }
        }

        // 4. Кнопка "Анализировать"
        private async void btnAnalyze_Click(object sender, EventArgs e)
        {
            await WithProgressAsync("Получение ключевых кадров...", async (dialog, ct) =>
            {
                await _viewModel.StartAnalysisAsync(
                    (current, total, found) =>
                    {
                        if (total == 0)
                            dialog.SetIndeterminate("Получение ключевых кадров...");
                        else
                            dialog.UpdateProgress(current, total, "Анализ видео...", found);
                    }, ct);
            });
        }

        // 5. Кнопка "Вырезать"
        private async void btnExport_Click(object sender, EventArgs e)
        {
            if (_viewModel.AnalyzedSegments == null || _viewModel.AnalyzedSegments.Count == 0)
            {
                MessageBox.Show("Сначала нажмите 'Анализировать'!", "Внимание", MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            // Сохраняем в ту же папку, что и оригинал, с суффиксом "_edit"
            string originalDir = Path.GetDirectoryName(_viewModel.VideoPath)!;
            string originalName = Path.GetFileNameWithoutExtension(_viewModel.VideoPath)!;
            string originalExt = Path.GetExtension(_viewModel.VideoPath)!;
            string outputFileName = Path.Combine(originalDir, $"{originalName}_edit{originalExt}");

            await WithProgressAsync("Подготовка к экспорту...", async (dialog, ct) =>
            {
                await _viewModel.ExportAsync(
                    outputFileName,
                    (current, total, found) => dialog.UpdateProgress(current, total, "Экспорт...", found),
                    ct);
            });
        }

        /// <summary>
        /// Очистка ресурсов при закрытии формы.
        /// </summary>
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _viewModel.Dispose();
            base.OnFormClosed(e);
        }
    }
}