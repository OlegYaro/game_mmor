# Изометрия и отрисовка больших карт в Godot 4 (C#)

Дата исследования: 2026-10-14. Актуальная стабильная ветка Godot — 4.7.x (4.7 stable вышла 19.06.2026, есть 4.7.1/4.7.2) [S1]. Проверено по docs.godotengine.org (stable), XML-докам и исходникам ветки 4.5 (`tile_map_layer.cpp`, `tile_set.cpp`), issue/PR Godot и форуму. Там, где источник — форум или блог, это помечено.

## Краткие выводы

1. `TileMapLayer` (4.3+) — единственный актуальный узел; `TileMap` deprecated [S2][S3]. Изометрия задаётся в `TileSet`: `tile_shape = Isometric`, `tile_layout`, `tile_offset_axis`, `tile_size = (64, 32)` [S4].
2. Y-sort в изометрии обязателен: документация говорит, что изометрический TileSet «works best if all sibling TileMapLayers and their parent inheriting from Node2D have Y-sort enabled» [S4]. Ключ сортировки тайла = `map_to_local(cell).y + TileData.y_sort_origin + TileMapLayer.y_sort_origin`; `texture_origin` в ключ **не входит** (исходник + независимый тест на 4.7) [S5][S6].
3. При включённом Y-sort `rendering_quadrant_size` игнорируется: движок создаёт отдельный CanvasItem **на каждое значение Y-ключа** (фактически на ряд), а после любого `set_cell` перестраивает весь ряд и переназначает draw index всем квадрантам слоя [S5]. Отсюда лаги `set_cell` на больших одиночных слоях: 4–5 мс на 700×700 в 4.5 (форум) [S7], падение в единицы FPS на 500×500 с Y-sort до разбиения на чанки (форум) [S8].
4. Для 512²–2048² брать **по `TileMapLayer` на чанк 32×32** с созданием/удалением вокруг камеры — это и рекомендация мейнтейнера Godot (Calinou: «for huge worlds, you need to create several TileMaps and load them in/out as needed») [S9].
5. Пакетного API вроде `set_cells(array)` нет; все изменения и так батчатся до конца кадра (`call_deferred`), поэтому N вызовов `SetCell` подряд = одна перестройка затронутых квадрантов [S2][S5]. `tile_map_data` (PackedByteArray) можно записать целиком, формат байтов в документации не описан.
6. Существа (300+): обычные `Sprite2D`/`Node2D` под общим Y-sorted родителем — сортируются с тайлами по `position.y`. `MultiMeshInstance2D` — один CanvasItem, с тайлами по Y не сортируется, подходит только для того, что всегда над/под картой [S10][S11]. Прямой `RenderingServer` даёт в тестах ±5% против `Sprite2D` [S12] — не стоит усложнения.
7. Туман войны: самый дешёвый вариант — полноэкранный `ColorRect`/`Sprite2D` с шейдером, читающим маску-текстуру (1 тексель = 1 тайл) и обновляемую `ImageTexture.Update()` только при изменении видимости [S13]. Свет/`CanvasModulate` стоят по пикселям на каждый источник [S14], слой тайлов тумана — перестройка рядов при каждом изменении.
8. Формула экран→тайл для Core: при `tile_layout = DiamondDown` это классическая изометрия (`px = 32(x−y)+32`, `py = 16(x+y)+16` для центра), обратное — линейно + округление [S5]. При `Stacked` (по умолчанию) формула другая («зигзаг» с половинным сдвигом нечётных рядов).

## Подробности

### 1. TileSet/TileMapLayer в изометрии и Y-sort

- `TileSet.tile_shape = TILE_SHAPE_ISOMETRIC` («diamond tile for an isometric look»), `tile_size` — «encompassing rectangle of the tile shape», т.е. `(64, 32)`. `tile_layout` для half-offset-форм «changes the way tiles are indexed»: `STACKED` (оси совпадают с экранными, нечётные ряды сдвинуты), `DIAMOND_DOWN` («horizontal axis runs down-right; vertical axis runs down-left» — классическая изометрия), и др. `tile_offset_axis` — `HORIZONTAL`/`VERTICAL` [S4].
- C#-имена enum'ов: генератор биндингов добавляет суффикс `Enum`, когда имя enum совпадает с именем свойства («we append 'Enum' to the enum name in those cases») [S15]. У `TileSet` есть свойства `TileShape`, `TileLayout`, `TileOffsetAxis`, поэтому в C# это `TileSet.TileShapeEnum.Isometric`, `TileSet.TileLayoutEnum.DiamondDown`, `TileSet.TileOffsetAxisEnum.Horizontal`.
- `CanvasItem.y_sort_enabled`: «this and child CanvasItem nodes with a higher Y position are rendered in front of nodes with a lower Y position»; сортируются только узлы с одинаковым `z_index` («Nodes sort relative to each other only if they are on the same z_index»). Если у родителя Y-sort включён, а у ребёнка нет — ребёнок сортируется, а его дети рисуются «together on the same Y position as the child node» [S16]. Это даёт приём «держатель на земле + смещённый вверх спрайт» для существ на возвышении.
- `TileData.y_sort_origin` — «vertical point of the tile used for determining y-sorted order»; `TileMapLayer.y_sort_origin` прибавляется ко всем тайлам слоя («allows, for example, to fake a different height level»); `x_draw_order_reversed` меняет порядок по X внутри ряда [S2][S17].
- Как сортируются спрайты против стен/деревьев: все `TileMapLayer`-чанки и узлы существ — дети одного `Node2D` с `YSortEnabled = true`, у каждого `TileMapLayer` тоже `YSortEnabled = true`. Тогда ключ тайла = `map_to_local(cell).y + y_sort_origin`, ключ спрайта = его `GlobalPosition.y`. Для высоких тайлов (стена, дерево) задавать `y_sort_origin` так, чтобы ключ лежал у визуального основания; `texture_origin` только двигает картинку [S6].
- Перепад высот 0–3. Проверенный на форуме (4.3) рецепт: по слою на уровень высоты, одинаковый `z_index` у всех слоёв, у каждого слоя свой `y_sort_origin`, Y-sort везде включён [S18]. Альтернатива без лишних слоёв (следует из ключа сортировки): кодировать высоту альтернативным тайлом с `texture_origin = (0, -16*h)` — ключ остаётся у ячейки земли, картинка поднимается. Официального руководства по многоуровневой изометрии в документации нет.

```csharp
// Scene: World (Node2D, YSortEnabled=true)
//   ├─ Chunk_0_0 (TileMapLayer, YSortEnabled=true, Position=(0,0))
//   ├─ ...
//   └─ Creatures (Node2D, YSortEnabled=true)
//        └─ Colonist (Node2D at ground cell pos, YSortEnabled=false)
//             └─ Sprite2D (Position = (0, -16 * height))  // drawn at the holder's Y
```

### 2. Производительность и лимиты TileMapLayer

- Квадранты: «A quadrant is a group of tiles to be drawn together on a single canvas item», по умолчанию 16×16 = 256 тайлов; «The quadrant size does not apply on a Y-sorted TileMapLayer, as tiles are grouped by Y position instead» [S2].
- Что происходит внутри (`tile_map_layer.cpp`, 4.5): при Y-sort `quadrant_coords = (0, map_to_local(coords).y + tile_y_sort_origin + y_sort_origin) * 100` — т.е. один «квадрант» на каждое значение Y-ключа. При обновлении грязного квадранта все его CanvasItem освобождаются и ряд перерисовывается целиком (`draw_tile` на каждую ячейку), новый CanvasItem создаётся при смене `material`/`z_index` тайла. В конце **каждого** обновления все квадранты слоя сортируются в `RBMap` и получают `canvas_item_set_draw_index` заново — O(число квадрантов) на любой `set_cell` [S5]. Для одного слоя 2048×2048 это ~4096 рядов (или 16384 квадранта без Y-sort) на каждое изменение; для чанка 32×32 — ~64 ряда.
- Координаты сериализуются как int16 (−32768..32767) — для 2048² не проблема [S2].
- Замеры (все — форум/issue, не официальные бенчмарки): 700×700 на 4.5 — 4–5 мс на `set_cell`, с физикой и Y-sort хуже [S7]; 500×500 с Y-sort — единицы FPS, после разбиения на чанки с включением по близости — ~120 FPS [S8]; issue #72458 (4.0 beta): сам размер карты почти не влияет на FPS (1500 vs 1700 FPS), мейнтейнер советует чанки [S9]. Большой TileSet (20+ атласов, ~200 МБ VRAM) давал 5–6 с задержку при загрузке сцены (4.4.1, без ответа) [S19].
- Физика: с 4.5 коллизии объединяются по `physics_quadrant_size` (PR #102662); нам физика не нужна — `CollisionEnabled = false`, `NavigationEnabled = false`, `OcclusionEnabled = false` на всех чанках [S2][S20].

### 3. Обновление тайлов из C#

- `SetCell(coords, sourceId, atlasCoords, alternativeTile)`; «all TileMap updates are batched at the end of a frame» через `call_deferred`; `UpdateInternals()` форсирует обновление сейчас («computationally expensive»); `NotifyRuntimeTileDataUpdate()` — только пометка на обновление в конце кадра для runtime-модификации `TileData` [S2][S5]. Батч-метода `set_cells` в API нет (запрос #31020 с Godot 3 не реализован) [S21]. `SetCellsTerrainConnect` медленнее одиночных вызовов на больших массивах (issue #69927) — для нашего проекта террейны не нужны, autotile считает Core [S22].
- Практика: копить изменения ячеек в Core за тик, в Godot применять одним проходом `SetCell` по изменённым ячейкам — движок сам перестроит только затронутые ряды. Массовая загрузка чанка: 32×32 = 1024 `SetCell` → одна перестройка чанка в конце кадра.
- `RenderingServer.CanvasItemAddTextureRectRegion(item, rect, textureRid, srcRect, ...)` — «Draws the specified region of a 2D textured rectangle on the CanvasItem» [S23]. Это ровно то, что `TileMapLayer` делает внутри, так что выигрыш — только от своей политики группировки (например, один CanvasItem на ряд чанка и перерисовка только его). Есть `CanvasItemSetSortChildrenByY` («Equivalent to CanvasItem.y_sort_enabled») и `CanvasItemSetCustomRect` для culling [S23]. Замеров «быстрее TileMapLayer» не нашёл; форумный тест 4.2.1 показал RS ≈ Sprite2D (2000 vs 2100 FPS) [S12].

```csharp
// Apply a batch of changed cells from Core to the chunk layer (one rebuild at end of frame).
foreach (var c in snapshot.ChangedCells)          // c: (X, Y, TileId, Height)
{
    var layer = chunks[new Vector2I(c.X >> 5, c.Y >> 5)];
    layer.SetCell(new Vector2I(c.X, c.Y), sourceId: 0,
                  atlasCoords: atlas[c.TileId], alternativeTile: c.Height);
}
```

### 4. Спрайты существ

- 300 `Sprite2D` — дёшево: в форумном тесте 4096 `Sprite2D` на пустой сцене давали ~250 FPS (4.2.1), 256 — ~2000 FPS [S12]. PR на динамические буферы instance data (#104566 → #111183) дал +10% на 20 000 прямоугольниках — упирается в CPU-overhead canvas items [S24].
- `MultiMeshInstance2D` «can be faster to render compared to displaying many Sprite2D nodes with large transparent areas»; одна текстура на MultiMesh, атлас выбирается через `INSTANCE_CUSTOM` в своём шейдере (`UseCustomData`) [S10][S11]. Но это один CanvasItem — Y-sort между экземплярами и тайлами невозможен (`y_sort_enabled` сортирует CanvasItem'ы) [S16]. Годится для частиц/декалей/иконок, не для жителей среди стен.
- Прямой `RenderingServer` рекомендуется документацией для «thousands of instances that need constant processing» [S25]; для 400 существ — преждевременно.

### 5. Туман войны

| Способ | Что это | Оценка |
|---|---|---|
| Слой `TileMapLayer` с полупрозрачными тайлами | `SetCell` на каждую открытую ячейку | Просто; каждое изменение перестраивает ряд и переназначает draw index [S5]; требует свой чанкинг |
| `CanvasModulate` + `PointLight2D` на каждого жителя | Затемнение + «свет» | «Larger lights have a higher performance cost as they affect more pixels on screen» [S14]; 100 источников — дорого; «запомненная» область не выражается |
| `LightOccluder2D` | Тени от стен | Решает line-of-sight, не туман; дорого при многих источниках [S14] |
| Шейдер + маска-текстура (1 тексель = тайл) | `ColorRect` поверх мира, в шейдере обратная изометрическая формула → `texture(mask, uv)` | Один draw call; обновление `ImageTexture.Update(image)` («faster than allocating additional memory for a new texture each time») только при изменении [S13]. Частичного (под-регион) обновления в публичном API не нашёл — грузится вся текстура (512² R8 = 256 КБ, 2048² = 4 МБ) |

Рекомендация: шейдер + маска, три состояния (неизвестно/видел/вижу) в одном канале; маску пишет Core, Godot копирует байты в `Image` и вызывает `Update`.

### 6. Камера и координаты

- `Camera2D.zoom`: «Vector2(2,2) will be twice as zoomed in… X and Y should generally always be set to the same value»; `limit_left/right/top/bottom` в пикселях, `limit_enabled`, `limit_smoothed` работает только с `position_smoothing_enabled`; `position_smoothing_speed` в px/с; шрифты при зуме мылятся — MSDF или `CanvasLayer` для UI [S26]. Ступени зума: `0.5, 1, 2` (целые кратности — пиксель-арт без артефактов); лимиты = bounding box ромба карты, пересчитывать при смене зума (документация не говорит, что `zoom` учитывается в лимитах).
- Godot: `layer.LocalToMap(layer.ToLocal(camera.GetGlobalMousePosition()))`, `MapToLocal` «returns the centered local position of a cell» и игнорирует `texture_origin` [S2].
- Чистый Core (из `tile_set.cpp`, `DIAMOND_DOWN`, `HORIZONTAL`, tile 64×32): `map_to_local = ((x−y)/2, x+y)`, затем `y *= 0.5`, затем `(ret + 0.5) * tile_size` [S5]:

```csharp
// Core (no Godot). Matches TileSet.map_to_local / local_to_map for DiamondDown + Horizontal.
const int W = 64, H = 32;
static (float px, float py) CellCenter(int x, int y) => ((x - y) * (W / 2f) + W / 2f, (x + y) * (H / 4f) + H / 2f);
static (int x, int y) CellAt(float px, float py)   // px,py in layer-local pixels
{
    float u = px / (W / 2f) - 1f;   // = x - y
    float v = py / (H / 4f) - 1f;   // = x + y   (H/4 because of the 0.5 overlap ratio)
    float fx = (u + v) * 0.5f, fy = (v - u) * 0.5f;
    return ((int)MathF.Floor(fx + 0.5f), (int)MathF.Floor(fy + 0.5f));
}
// Height: draw at (px, py - 16 * h); sort key stays py.
```

Для `STACKED` (по умолчанию) ряды идут по экранному Y с половинным сдвигом нечётных, и `local_to_map` делает проверку «в каком треугольнике ромба» — формула сложнее [S5]. Берём `DiamondDown`, чтобы Core и Godot считали одинаково; проверить тестом `LocalToMap(MapToLocal(c)) == c` на старте.

### 7. Процедурный TileSet из PNG-атласа

`TileSetAtlasSource.create_tile(atlas_coords, size)` — «Creates a new tile at coordinates atlas_coords with the given size»; `texture_region_size` ≥ `TileSet.tile_size`; `TileSet.add_source(source, id)` возвращает ID; `use_texture_padding` (по умолчанию true, можно выключить ради небольшой производительности) [S4][S27]. Альтернативные тайлы — `create_alternative_tile`, данные — `get_tile_data(coords, alt)` [S27].

```csharp
public static TileSet BuildTileSet(Texture2D atlas, int columns, int rows, int heightLevels)
{
    var ts = new TileSet {
        TileShape = TileSet.TileShapeEnum.Isometric,
        TileLayout = TileSet.TileLayoutEnum.DiamondDown,
        TileOffsetAxis = TileSet.TileOffsetAxisEnum.Horizontal,
        TileSize = new Vector2I(64, 32) };
    var src = new TileSetAtlasSource { Texture = atlas, TextureRegionSize = new Vector2I(64, 64) };
    ts.AddSource(src, 0);
    for (int y = 0; y < rows; y++)
    for (int x = 0; x < columns; x++)
    {
        var c = new Vector2I(x, y);
        src.CreateTile(c);
        src.GetTileData(c, 0).TextureOrigin = new Vector2I(0, -16);     // 64x64 art on a 64x32 cell
        for (int h = 1; h < heightLevels; h++)
        {
            int alt = src.CreateAlternativeTile(c);
            src.GetTileData(c, alt).TextureOrigin = new Vector2I(0, -16 - 16 * h);
        }
    }
    return ts;
}
```

Контракт для замены арта: атлас — PNG с сеткой 64×64 (ромб в нижних 32 px, верх — для стен/высоты), индекс тайла = `(id % columns, id / columns)`; смена PNG не трогает код. Заглушку генерирует скрипт в `Image` → `ImageTexture.CreateFromImage` [S13]. C#-синтаксис выше по документации GDScript-API плюс правило PascalCase/`Enum` биндингов [S15]; примера на C# в официальной документации нет.

## Сравнение вариантов отрисовки карты

| Вариант | Плюсы | Минусы | Когда брать |
|---|---|---|---|
| Один `TileMapLayer` на весь мир | Проще всего; `LocalToMap` глобальный | Y-sort → тысячи ряд-CanvasItem'ов; каждый `set_cell` переназначает draw index всем (O(рядов)) [S5]; замеры 4–5 мс/`set_cell` на 700² [S7] | Прототип ≤ 256² |
| `TileMapLayer` на чанк 32×32, создаются/освобождаются вокруг камеры | Цена `set_cell` ограничена чанком (~64 рядов); загрузка/выгрузка — дешёвый `QueueFree`/`AddChild`; рекомендовано мейнтейнером [S9]; Y-sort через общего родителя работает между чанками и спрайтами [S4][S16] | Свой менеджер чанков; все чанки с Y-sort и одинаковым `z_index` | Наш случай 512²–2048² |
| `MultiMeshInstance2D` на чанк | Один draw call на чанк, минимум CPU [S10] | Нет Y-sort с существами [S16]; ручной порядок рисования (сортировать экземпляры по строкам самому); одна текстура; свой шейдер для атласа [S11] | Плоская земля без перекрытий (только слой «пол»), декали, туман |
| `RenderingServer` напрямую (CanvasItem на ряд чанка) | Полный контроль над группировкой и перерисовкой; та же RS-команда, что внутри TileMapLayer [S23] | Пишем свой TileMapLayer; ≈ та же скорость, что узлы (±5%) [S12] | Если профилировщик покажет, что перестройка рядов TileMapLayer — узкое место |
| GPU-тайлмап (карта как текстура + шейдер) [S28] | Стоимость зависит только от пикселей экрана | Сторонний аддон, без изометрии и без Y-sort; один атлас | Не для этого проекта |

## Рекомендация для проекта

1. `TileSet`: `Isometric`, `DiamondDown`, `Horizontal`, `TileSize (64,32)`, атлас 64×64 с `TextureOrigin (0,-16)`; высота 0–3 — альтернативные тайлы со сдвигом `-16*h`. Core считает координаты формулой из §6; на старте тест-сверка с `MapToLocal/LocalToMap`.
2. Мир = `Node2D` с Y-sort; дети — `TileMapLayer`-чанки 32×32 (ячейки в абсолютных координатах, `Position = 0`, Y-sort включён, `z_index = 0`, физика/навигация/окклюзия выключены) и контейнер существ. Отдельный слой «земля без перекрытий» можно вынести в не-Y-sorted чанки с `rendering_quadrant_size = 32` (один CanvasItem на чанк), а стены/деревья/объекты — в Y-sorted чанки.
3. Менеджер чанков держит видимые + кольцо в 1 чанк; создание чанка = 1024 `SetCell` в одном кадре; изменения из Core применяются батчем раз в кадр.
4. Существа: `Node2D`-держатель на уровне земли + дочерний `Sprite2D` со сдвигом по высоте; 300–400 узлов — некритично [S12]. MultiMesh — только для эффектов.
5. Туман: `ColorRect` на верхнем `z_index` (или `CanvasLayer`, следующий за камерой) с шейдером и маской R8 512×512 из Core; `ImageTexture.Update` по dirty-флагу.
6. Камера: ступени `0.5/1/2`, `limit_*` по bounding box ромба, `position_smoothing` по вкусу; UI на `CanvasLayer`.
7. Профилировать на 2048² заранее: число CanvasItem (ряды × чанки на экране) и время кадра после массового `SetCell`; если ряд-перестройка окажется дорогой — переход на собственные CanvasItem через `RenderingServer` с той же сценой.

## Источники

- [S1] Godot 4.7 stable (19.06.2026) и патчи 4.7.1/4.7.2 — https://newreleases.io/project/github/godotengine/godot/release/4.7-stable , https://www.warp2search.net/story/godot-472-release-threading-hardening-mouse-input-fixes-and-57-stability-patches/
- [S2] TileMapLayer class reference (stable) — https://docs.godotengine.org/en/stable/classes/class_tilemaplayer.html
- [S3] Godot 4.3 release notes, TileMapLayer — https://godotengine.org/releases/4.3/
- [S4] TileSet class reference (tile_shape/tile_layout/tile_offset_axis, isometric note) — https://docs.godotengine.org/en/stable/classes/class_tileset.html
- [S5] Исходники 4.5: `scene/2d/tile_map_layer.cpp` (`_rendering_quadrants_update_cell`, `_rendering_update`, `_queue_internal_update`), `scene/resources/2d/tile_set.cpp` (`map_to_local`, `local_to_map`) — https://github.com/godotengine/godot/blob/4.5/scene/2d/tile_map_layer.cpp , https://github.com/godotengine/godot/blob/4.5/scene/resources/2d/tile_set.cpp
- [S6] Тест ключа Y-sort на 4.7 (блог, 28 проверок) — https://blobsmith.itch.io/blobsmith/devlog/1634806/your-godot-4-tiles-are-y-sorted-and-still-draw-in-the-wrong-order-the-field-everyone-drags-is-not-in-the-sort-key
- [S7] Форум: 700×700, 4–5 мс на set_cell, Godot 4.5 — https://forum.godotengine.org/t/large-2d-tilemap-causes-4-5ms-lag-on-setcell-invocation/126593
- [S8] Форум: 500×500 с Y-sort, чанки вернули 120 FPS — https://forum.godotengine.org/t/tilemap-quadrant-zize-vs-chunks-performance/3425
- [S9] Issue #72458, комментарий Calinou про чанки — https://github.com/godotengine/godot/issues/72458
- [S10] MultiMeshInstance2D — https://docs.godotengine.org/en/stable/classes/class_multimeshinstance2d.html
- [S11] MultiMesh (use_custom_data, INSTANCE_CUSTOM) — https://docs.godotengine.org/en/stable/classes/class_multimesh.html
- [S12] Форум: Sprite2D vs RenderingServer, 4.2.1 — https://forum.godotengine.org/t/2d-rendering-performance/45719
- [S13] ImageTexture.update / set_image — https://docs.godotengine.org/en/stable/classes/class_imagetexture.html
- [S14] 2D lights and shadows (CanvasModulate, стоимость света) — https://docs.godotengine.org/en/stable/tutorials/2d/2d_lights_and_shadows.html
- [S15] Правило суффикса `Enum` в C#-биндингах — https://github.com/godotengine/godot/blob/4.5/modules/mono/editor/bindings_generator.cpp
- [S16] CanvasItem (y_sort_enabled, z_index) — https://docs.godotengine.org/en/stable/classes/class_canvasitem.html
- [S17] TileData (y_sort_origin, texture_origin, z_index) — https://docs.godotengine.org/en/stable/classes/class_tiledata.html
- [S18] Форум: многоуровневая изометрия, 4.3 — https://forum.godotengine.org/t/unable-to-properly-y-sort-entities-in-a-multi-leveled-isometric-tilemap/93061
- [S19] Форум: большой TileSet, 5–6 с загрузка — https://forum.godotengine.org/t/performance-issues-of-tilemaplayer-when-using-a-large-tileset/113746
- [S20] PR #102662 «Chunk tilemap physics» (4.5) — https://github.com/godotengine/godot/pull/102662
- [S21] Issue #31020 (запрос batch set_cell) — https://github.com/godotengine/godot/issues/31020
- [S22] Issue #69927 (set_cells_terrain_connect медленнее) — https://github.com/godotengine/godot/issues/69927
- [S23] RenderingServer (canvas_item_add_texture_rect_region, sort_children_by_y, custom_rect) — https://docs.godotengine.org/en/stable/classes/class_renderingserver.html
- [S24] PR #104566 (dynamic buffers, +10% на 20k rect) — https://github.com/godotengine/godot/pull/104566
- [S25] Optimization using servers — https://docs.godotengine.org/en/stable/tutorials/performance/using_servers.html
- [S26] Camera2D — https://docs.godotengine.org/en/stable/classes/class_camera2d.html
- [S27] TileSetAtlasSource — https://docs.godotengine.org/en/stable/classes/class_tilesetatlassource.html
- [S28] GPU Tilemap addon (сторонний) — https://fabianlc.itch.io/godot-gpu-tilemap-addon
