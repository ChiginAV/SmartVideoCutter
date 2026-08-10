namespace SmartVideoCutter.Services
{
    /// Фасад над ffprobe и ffmpeg. Делегирует вызовы в специализированные сервисы.
    public class VideoProcessor
    {
        private readonly FfprobeService _ffprobe;
        private readonly FfmpegEncoder _ffmpeg;

        public VideoProcessor()
        {
            _ffprobe = new FfprobeService();
            _ffmpeg = new FfmpegEncoder();
        }

        // --- ffprobe ---
        public List<int> GetKeyFrames(string videoPath, double fps, CancellationToken cancelToken = default)
            => _ffprobe.GetKeyFrames(videoPath, fps, cancelToken);

        public List<int> GetKeyFramesByFrames(string videoPath, double fps, int totalFrames = 0, Action<int>? progressCallback = null)
            => _ffprobe.GetKeyFramesByFrames(videoPath, fps, totalFrames, progressCallback);

        public double GetFPS(string videoPath) => _ffprobe.GetFPS(videoPath);

        // --- ffmpeg ---
        public void CutSegment(string inputPath, string outputPath, int startFrame, int endFrame, double fps)
            => _ffmpeg.CutSegment(inputPath, outputPath, startFrame, endFrame, fps);

        public async Task CutSegmentAsync(string inputPath, string outputPath, int startFrame, int endFrame, double fps, CancellationToken cancelToken = default)
            => await _ffmpeg.CutSegmentAsync(inputPath, outputPath, startFrame, endFrame, fps, cancelToken);

        public void JoinSegments(string listFilePath, string finalOutputPath)
            => _ffmpeg.JoinSegments(listFilePath, finalOutputPath);

        public async Task JoinSegmentsAsync(string listFilePath, string finalOutputPath, CancellationToken cancelToken = default)
            => await _ffmpeg.JoinSegmentsAsync(listFilePath, finalOutputPath, cancelToken);
    }
}