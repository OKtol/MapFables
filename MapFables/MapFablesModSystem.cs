using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace MapFables;

/// <summary>
/// Точка входа мода. Регистрирует MapFablesChunkLayer в реестре слоёв карты
/// ДО того как WorldMapManager создаёт экземпляры слоёв в OnLvlFinalize.
///
/// Порядок: WorldMapManager.Start() регистрирует дефолтные слои,
/// наш Start() вызывается следом и добавляет свой слой в тот же реестр.
/// При LevelFinalize движок создаёт все слои через Activator.CreateInstance.
/// </summary>
public class MapFablesModSystem : ModSystem
{
    public const string MapLayerCode = "mapfables_chunks";

    public override void Start(ICoreAPI api)
    {
        base.Start(api);

        var wmm = api.ModLoader.GetModSystem<WorldMapManager>();
        wmm.RegisterMapLayer<MapFablesChunkLayer>(MapLayerCode, 0.05);

        api.Logger.Notification("[MapFables] Registered MapFablesChunkLayer.");
    }
}
