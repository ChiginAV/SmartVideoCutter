namespace SmartVideoCutter.Models;

/// Алгоритм анализа видео.
public enum AppAnalysisAlgorithm
{
    /// Точный: ~3 кадра в секунду (шаг floor(fps/3)) + финальный кадр отрезка.
    ThreePerSecond,

    /// Быстрый: 3 кадра на отрезок — ключевой, средний, последний перед следующим ключевым.
    ThreeBetweenKeyframes
}
