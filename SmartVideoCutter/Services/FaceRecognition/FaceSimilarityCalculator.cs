namespace SmartVideoCutter.Services.FaceRecognition;

public static class FaceSimilarityCalculator
{
    /// Евклидово расстояние между двумя L2-нормализованными векторами.
    /// Результат ∈ [0, 2]. Меньше = более похожи.
    public static double EuclideanDistance(float[] vector1, float[] vector2)
    {
        double sum = 0;
        for (int i = 0; i < vector1.Length; i++)
        {
            double diff = vector1[i] - vector2[i];
            sum += diff * diff;
        }
        return Math.Sqrt(sum);
    }
}