# Smart video cutter

Feature description (soon... maybe)

## Downloads

FFmpeg:

- https://ffmpeg.org/download.html
- https://github.com/GyanD/codexffmpeg/releases

YOLO:

- https://huggingface.co/deepghs/yolo-face

ArcFace (InsightFace)

- https://huggingface.co/onnx-community/arcface-onnx

---

## Documentation

FFmpeg:

- https://ffmpeg.org/documentation.html
- https://ffmpeg.org/ffprobe-all.html

YOLO:

- https://docs.ultralytics.com/
- https://yolo-docs.readthedocs.io/en/latest/
- https://github.com/ultralytics/ultralytics

ArcFace (InsightFace):

- https://www.insightface.ai/research/arcface
- https://www.insightface.ai/guides
- https://github.com/deepinsight/insightface/blob/master/detection/README.md
- https://github.com/deepinsight/insightface/blob/master/recognition/README.md

Flyleaf:

- https://github.com/SuRGeoNix/Flyleaf
- https://github.com/SuRGeoNix/Flyleaf/wiki
- https://github.com/SuRGeoNix/Flyleaf/tree/master/Samples

---

# Terms

__Bounding Box (Ограничивающая рамка)__ - Прямоугольник вокруг найденного объекта. Показывает координаты предмета на фото.

__Channel (Канал)__ - Слой цвета в картинке. Обычное цветное фото имеет три канала (RGB)

__Distance (Расстояние)__ - Мера схожести двух векторов (обычно Евклидово расстояние или косинусное сходство).
Чем меньше расстояние между двумя цифровыми «паспортами», тем выше вероятность, что это один и тот же человек.

__Embedding / Vector (Эмбеддинг или Вектор лица)__ - Набор из 128 или 512 чисел, который описывает уникальные черты лица. 
У похожих людей эти числа почти одинаковые, у разных — сильно отличаются.

__Face detection (Детекция лиц)__

__Face recognition (Распознавание лиц)__

__Landmarks (Ключевые точки)__ - Важные координаты на лице: центры глаз, кончик носа, уголки губ, контур подбородка

__Mat (Матрица)__ - Прямоугольная сетка из чисел
- Черно-белое фото — это одна таблица чисел.
- Цветное фото — это три таблицы (красный, зеленый, синий цвета).

__Tensor (Тензор)__ - Общее имя для таблиц с разным числом измерений
- 1D-тензор - вектор
- 2D-тензор - матрица (обычная таблица)
- 3D-тензор - куб
- 4+D-тензор...

__Threshold (Порог)__ - граница, при которой лица считаются одинаковыми. Ниже - разные люди.