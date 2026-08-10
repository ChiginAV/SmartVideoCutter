using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using System.Runtime.InteropServices;

namespace SmartVideoCutter.Services.Preprocessing;

/// Конвертирует OpenCV Mat в DenseTensor для ONNX-моделей.
/// Использует переиспользуемые буферы для минимизации аллокаций.
public class TensorConverter
{
    private readonly byte[] _byteBuffer;
    private readonly float[] _floatData;

    public TensorConverter(int width, int height)
    {
        _byteBuffer = new byte[width * height * 3];
        _floatData = new float[width * height * 3];
    }

    /// Конвертирует BGR Mat в планарный тензор [1,3,H,W] (NCHW), диапазон [0,1].
    /// Используется для YOLO-Face.
    public DenseTensor<float> ConvertToNchw(Mat img, int targetWidth, int targetHeight)
    {
        Marshal.Copy(img.Ptr(0), _byteBuffer, 0, targetWidth * targetHeight * 3);

        int hw = targetHeight * targetWidth;

        // BGR -> RGB, нормализация pixel/255 → диапазон [0, 1]
        for (int i = 0; i < targetWidth * targetHeight * 3; i += 3)
        {
            int idx = i / 3;
            _floatData[idx] = _byteBuffer[i + 2] / 255f;           // R plane
            _floatData[hw + idx] = _byteBuffer[i + 1] / 255f;      // G plane
            _floatData[2 * hw + idx] = _byteBuffer[i] / 255f;      // B plane
        }

        return new DenseTensor<float>(_floatData, new[] { 1, targetHeight, targetWidth });
    }

    /// Конвертирует BGR Mat в тензор [1,H,W,3] (NHWC), диапазон [0,1].
    /// Используется для ArcFace.
    public DenseTensor<float> ConvertToNhwc(Mat img, int size)
    {
        Marshal.Copy(img.Ptr(0), _byteBuffer, 0, size * size * 3);

        int totalPixels = size * size;
        int pixelIndex = 0;
        for (int i = 0; i < totalPixels * 3; i += 3)
        {
            _floatData[pixelIndex++] = _byteBuffer[i + 2] / 255f; // R
            _floatData[pixelIndex++] = _byteBuffer[i + 1] / 255f; // G
            _floatData[pixelIndex++] = _byteBuffer[i] / 255f;     // B
        }

        return new DenseTensor<float>(_floatData, new[] { 1, size, size, 3 });
    }
}