namespace SmartVideoCutter.Views
{
    partial class MainForm
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            picFrame = new PictureBox();
            btnOpen = new Button();
            btnAnalyze = new Button();
            btnExport = new Button();
            lblStatus = new Label();
            trkTimeline = new TrackBar();
            ((System.ComponentModel.ISupportInitialize)picFrame).BeginInit();
            ((System.ComponentModel.ISupportInitialize)trkTimeline).BeginInit();
            SuspendLayout();
            // 
            // picFrame
            // 
            picFrame.Location = new Point(12, 12);
            picFrame.Name = "picFrame";
            picFrame.Size = new Size(1228, 909);
            picFrame.SizeMode = PictureBoxSizeMode.Zoom;
            picFrame.TabIndex = 0;
            picFrame.TabStop = false;
            picFrame.MouseClick += PicFrame_MouseClick;
            // 
            // btnOpen
            // 
            btnOpen.Location = new Point(1248, 16);
            btnOpen.Name = "btnOpen";
            btnOpen.Size = new Size(236, 50);
            btnOpen.TabIndex = 1;
            btnOpen.Text = "Открыть видео";
            btnOpen.UseVisualStyleBackColor = true;
            btnOpen.Click += btnOpen_Click;
            // 
            // btnAnalyze
            // 
            btnAnalyze.Enabled = false;
            btnAnalyze.Location = new Point(1248, 111);
            btnAnalyze.Name = "btnAnalyze";
            btnAnalyze.Size = new Size(236, 50);
            btnAnalyze.TabIndex = 1;
            btnAnalyze.Text = "Анализировать";
            btnAnalyze.UseVisualStyleBackColor = true;
            btnAnalyze.Click += btnAnalyze_Click;
            // 
            // btnExport
            // 
            btnExport.Enabled = false;
            btnExport.Location = new Point(1248, 218);
            btnExport.Name = "btnExport";
            btnExport.Size = new Size(236, 50);
            btnExport.TabIndex = 1;
            btnExport.Text = "Экспортировать видео";
            btnExport.UseVisualStyleBackColor = true;
            btnExport.Click += btnExport_Click;
            // 
            // lblStatus
            // 
            lblStatus.Location = new Point(1248, 317);
            lblStatus.Name = "lblStatus";
            lblStatus.Size = new Size(236, 175);
            lblStatus.TabIndex = 2;
            lblStatus.Text = "label1";
            // 
            // trkTimeline
            // 
            trkTimeline.Location = new Point(12, 927);
            trkTimeline.Name = "trkTimeline";
            trkTimeline.Size = new Size(1228, 69);
            trkTimeline.TabIndex = 3;
            trkTimeline.Scroll += trkTimeline_Scroll;
            // 
            // MainForm
            // 
            AutoScaleDimensions = new SizeF(10F, 25F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(1496, 1008);
            Controls.Add(trkTimeline);
            Controls.Add(lblStatus);
            Controls.Add(btnExport);
            Controls.Add(btnAnalyze);
            Controls.Add(btnOpen);
            Controls.Add(picFrame);
            Name = "MainForm";
            StartPosition = FormStartPosition.CenterScreen;
            Text = "Smart video cutter";
            ((System.ComponentModel.ISupportInitialize)picFrame).EndInit();
            ((System.ComponentModel.ISupportInitialize)trkTimeline).EndInit();
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private PictureBox picFrame;
        private Button btnOpen;
        private Button btnAnalyze;
        private Button btnExport;
        private Label lblStatus;
        private TrackBar trkTimeline;
    }
}
