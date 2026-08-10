using System;
using System.Diagnostics;
using System.Windows.Forms;

namespace SmartVideoCutter.Views
{
    /// <summary>
    /// Переиспользуемое модальное окно для отображения прогресса длительных операций.
    /// Поддерживает кнопку "Отмена" через CancellationTokenSource.
    /// </summary>
    public class ProgressDialog : Form
    {
        private ProgressBar _progressBar;
        private Label _labelStatus;
        private Button _btnCancel;
        private readonly Stopwatch _throttleWatch = new Stopwatch();
        private const int MinUpdateMs = 50; // Минимальный интервал между обновлениями UI (мс) — уменьшено для более отзывчивого прогресса

        public CancellationTokenSource Cts { get; } = new CancellationTokenSource();

        public ProgressDialog()
        {
            InitializeComponentCustom();
        }

        private void InitializeComponentCustom()
        {
            this.Text = "Выполняется операция...";
            this.Size = new System.Drawing.Size(460, 175);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowInTaskbar = false;

            // ProgressBar
            _progressBar = new ProgressBar
            {
                Name = "progressBar",
                Location = new System.Drawing.Point(20, 15),
                Size = new System.Drawing.Size(420, 23),
                Maximum = 100,
                Value = 0
            };

            // Label для статуса
            _labelStatus = new Label
            {
                Name = "labelStatus",
                Location = new System.Drawing.Point(20, 48),
                Size = new System.Drawing.Size(420, 20),
                Text = "Подготовка...",
                TextAlign = ContentAlignment.MiddleCenter
            };

            // Кнопка Отмена
            _btnCancel = new Button
            {
                Name = "btnCancel",
                Location = new System.Drawing.Point(180, 78),
                Size = new System.Drawing.Size(100, 32),
                Text = "Отмена"
            };
            _btnCancel.Click += Btncancel_Click;

            this.Controls.AddRange(new Control[] { _progressBar, _labelStatus, _btnCancel });
        }

        private void Btncancel_Click(object? sender, EventArgs e)
        {
            _btnCancel.Enabled = false; // предотвращаем повторные клики
            Cts.Cancel();
        }

        /// <summary>
        /// Обновить прогресс (определяемый, с процентами).
        /// Использует Invoke для обновления с фоновых потоков.
        /// Первый вызов (переключение из индетерминированного режима) — всегда выполняется без throttling.
        /// </summary>
        /// <param name="current">Текущий шаг</param>
        /// <param name="total">Общее количество шагов</param>
        /// <param name="message">Сообщение о текущей операции</param>
        /// <param name="foundSegments">Количество найденных сегментов (опционально)</param>
        public void UpdateProgress(int current, int total, string message = "", int foundSegments = -1)
        {
            if (this.IsDisposed)
                return;

            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() => UpdateProgress(current, total, message, foundSegments)));
                return;
            }

            int percent = total > 0 ? (int)(100.0 * current / total) : 0;

            // Throttling: пропускаем ЧАСТОТНЫЕ обновления, но первый вызов (смена фазы) — всегда выполняем
            bool isFirstUpdate = _progressBar.Style == ProgressBarStyle.Marquee;

            if (!isFirstUpdate && _throttleWatch.ElapsedMilliseconds < MinUpdateMs)
                return;

            _throttleWatch.Restart();
            _progressBar.Style = ProgressBarStyle.Continuous;
            _progressBar.Value = Math.Min(percent, 100);

            // Label: сообщение + прогресс + найденные сегменты (всегда отображается, даже 0)
            string progressPart = total > 0 ? $"{current}/{total}" : "…";
            string baseText = !string.IsNullOrEmpty(message)
                ? $"{message} {progressPart}"
                : progressPart;

            // Всегда показываем количество найденных сегментов (включая 0)
            if (foundSegments >= 0)
                _labelStatus.Text = $"{baseText} | Найдено: {foundSegments}";
            else
                _labelStatus.Text = baseText;
        }

        ///
        public void SetIndeterminate(string message)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action(() => SetIndeterminate(message)));
                return;
            }

            _progressBar.Style = ProgressBarStyle.Marquee;
            _labelStatus.Text = message;
        }
    }
}