using System.Text.Json;
using System.Text.Json.Serialization;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.DI.Routing;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Request;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Inventory;
using SPTarkov.Server.Core.Models.Eft.Inventory;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Services.Commerce;
using SPTarkov.Server.Core.Utils;
using Path = System.IO.Path;

namespace PerkBeveragesServer;

public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.kennerz.perkbeverages"; // same GUID as the client plugin (Forge rule)
    public string Name { get; init; } = "PerkBeverages";
    public string Author { get; init; } = "Kenner";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("0.1.0");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.0");
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; } = "https://github.com/Kennerz77/KennerPerkBeveragesSPT";
    public string License { get; init; } = "MIT";
    public bool HasPrepatcher { get; init; } = false;
}

/// <summary>Settings read from config.json next to the DLL. Never shipped, so an update can't overwrite it.</summary>
public record PerkConfig
{
    /// <summary>What a perk costs at its machine, in roubles.</summary>
    public int PerkPrice { get; init; } = 25_000;

    /// <summary>A perk bottle's handbook value, in roubles (nobody sells them: zombies mode only).</summary>
    public int BottlePrice { get; init; } = 25_000;

    internal static PerkConfig Load(Action<string> logError)
    {
        string path = Path.Combine(Path.GetDirectoryName(typeof(PerkConfig).Assembly.Location)!, "config.json");
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<PerkConfig>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception e)
        {
            logError($"[PerkBeverages] Could not read {path}, using defaults: {e.Message}");
        }
        return new();
    }
}

/// <summary>Body of the client's purchase at a perk machine.</summary>
public record PerkBuyRequest : BaseInteractionRequestData
{
    /// <summary>The perk's id (its machine's), e.g. "jugg".</summary>
    [JsonPropertyName("perk")]
    public string? Perk { get; set; }

    /// <summary>The client can drink it now: the bottle goes on the character (pockets, vest, backpack) to be drunk.</summary>
    [JsonPropertyName("carry")]
    public bool Carry { get; set; }
}

/// <summary>
/// The hideout's perk machines: paid in roubles from the stash, the machine drops a bottle of the perk, which goes on
/// the character for the client to drink on the spot (the original's drink). What the perk does is the client's.
/// </summary>
[Injectable(InjectionType.Singleton)]
public class PerkShop(ISptLogger<PerkShop> logger, PaymentService paymentService, HttpResponseUtil httpResponseUtil,
    InventoryHelper inventoryHelper, ItemHelper itemHelper)
{
    public const string BuyAction = "PerkBuy";
    private const string Roubles = "5449016a4bdc2d6f028b456f";

    private static readonly Dictionary<string, MongoId> Bottles = PerkIds.Perks.ToDictionary(p => p.Id, p => p.Bottle);
    private static readonly string[] CarrySlots = ["Pockets", "TacticalVest", "Backpack"];

    private readonly PerkConfig _config = PerkConfig.Load(message => logger.Error(message));

    public ItemEventRouterResponse Buy(PmcData pmcData, PerkBuyRequest body, MongoId sessionId, ItemEventRouterResponse output)
    {
        if (body.Perk == null || !Bottles.TryGetValue(body.Perk, out MongoId template))
            return httpResponseUtil.AppendErrorToOutput(output, "That machine sells nothing.");
        // With carry, the bottle goes on the character to be drunk (not used for now: an item added on the character in
        // the hideout hung the client's reply handling, and the hideout doesn't let you drink anyway)
        Item? bottle = null;
        if (body.Carry)
        {
            bottle = new Item { Id = new MongoId(), Template = template, Upd = new Upd { StackObjectsCount = 1 } };
            if (!FindCarriedSpot(pmcData, bottle))
                return httpResponseUtil.AppendErrorToOutput(output, "No room in your pockets, rig or backpack for the bottle.");
        }

        int price = _config.PerkPrice;
        double roubles = pmcData.Inventory?.Items?.Where(i => i.Template.ToString() == Roubles).Sum(i => i.Upd?.StackObjectsCount ?? 1) ?? 0;
        if (roubles < price)
            return httpResponseUtil.AppendErrorToOutput(output, "Not enough funds.");

        // The 5-argument overload exists on every SPT 4.1.x (the newer one with requestedStackIds may not); it only
        // passes null for those stack ids, so it behaves the same. Marked obsolete for removal in a later SPT.
#pragma warning disable CS0618
        paymentService.AddPaymentToOutput(pmcData, Money.ROUBLES, price, sessionId, output);
#pragma warning restore CS0618
        if (output.Warnings?.Count > 0)
            return output;
        if (bottle != null)
            AddCarried(pmcData, bottle, sessionId, output);
        logger.Info($"[PerkBeverages] {pmcData.Info?.Nickname} bought {body.Perk} for {price} RUB");
        return output;
    }

    /// <summary>The containers the character wears (pockets, vest, backpack).</summary>
    private static List<Item> CarryContainers(PmcData pmcData)
    {
        string? equipment = pmcData.Inventory?.Equipment?.ToString();
        if (equipment == null || pmcData.Inventory?.Items == null)
            return [];
        return CarrySlots.Select(s => pmcData.Inventory.Items.FirstOrDefault(i => i.ParentId == equipment && i.SlotId == s)).OfType<Item>().ToList();
    }

    /// <summary>
    /// A free spot for the bottle in the character's containers (pockets, then vest, then backpack): sets the bottle's
    /// parent, grid and location. Nothing is added to the profile yet. (As the Ray Gun's cells.)
    /// </summary>
    private bool FindCarriedSpot(PmcData pmcData, Item bottle)
    {
        foreach (Item container in CarryContainers(pmcData))
        {
            KeyValuePair<bool, TemplateItem?> template = itemHelper.GetItem(container.Template);
            foreach (Grid grid in template.Value?.Properties?.Grids ?? [])
            {
                if (grid.Name == null || grid.Properties?.CellsH == null || grid.Properties.CellsV == null)
                    continue;
                IEnumerable<Item> inGrid = pmcData.Inventory!.Items!.Where(i => i.ParentId == container.Id.ToString() && i.SlotId == grid.Name);
                int[,] map = inventoryHelper.GetContainerMap(grid.Properties.CellsH.Value, grid.Properties.CellsV.Value, inGrid, container.Id);
                FindSlotResult result = inventoryHelper.PlaceItemInContainer(map, [bottle], container.Id.ToString(), grid.Name);
                if (result.Success == true)
                    return true;
            }
        }
        return false;
    }

    /// <summary>The bottle, already given its spot on the character, added to the profile and to the reply.</summary>
    private static void AddCarried(PmcData pmcData, Item bottle, MongoId sessionId, ItemEventRouterResponse output)
    {
        pmcData.Inventory!.Items!.Add(bottle);
        if (output.ProfileChanges?.TryGetValue(sessionId, out ProfileChange? change) == true && change?.Items != null)
        {
            change.Items.NewItems ??= [];
            change.Items.NewItems.Add(bottle);
        }
    }
}

/// <summary>Routes the client's PerkBuy item event.</summary>
[Injectable(TypePriority = OnLoadOrder.Routers)]
public class PerkShopRouter(PerkShop shop) : ItemEventRouter([
    new ItemRouteAction<PerkBuyRequest>(PerkShop.BuyAction,
        (url, pmcData, body, sessionID, output, cancellationToken) => ValueTask.FromResult(shop.Buy(pmcData, body, sessionID, output))),
]) { }
