using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Items;
using SPTarkov.Server.Core.Services.Modding.Custom;
using Path = System.IO.Path;

namespace PerkBeveragesServer;

/// <summary>Fixed template ids. They share a prefix so the mod's items are easy to spot in any item list.</summary>
internal static class PerkIds
{
    public const string Prefix = "6a7f9e4b0c0a115000000";

    /// <summary>One perk's bottle (item ...b&lt;nn&gt;), its Therapist offer (...a&lt;nn&gt;) and its texts.</summary>
    public record Perk(string Id, string ItemName, string Name, string ShortName, string Description, MongoId Bottle, MongoId Offer, string Background);

    /// <param name="background">The inventory cell colour (EFT's names: red, green, yellow, orange, blue, violet, black...)</param>
    private static Perk Make(string nn, string id, string name, string shortName, string background, string slogan) =>
        new(id, "item_drink_perk_" + id, name, shortName,
            $"A bottle of {name} from Dr K's Perk Beverages. {slogan} Best enjoyed cold, straight from the machine.",
            new MongoId(Prefix + "b" + nn), new MongoId(Prefix + "a" + nn), background);

    /// <summary>Every perk, in the order they were added. Ids are fixed forever (saved profiles hold them).</summary>
    public static readonly Perk[] Perks =
    [
        Make("01", "jugg", "Jugger-Nog", "Jugg", "red", "\"When you need some help to get by, something to make you feel strong... Reach for Jugger-Nog tonight!\""),
        Make("02", "speed", "Speed Cola", "Speed", "green", "\"Reload faster, hustle harder. Speed Cola!\""),
        Make("03", "dtap", "Double Tap Root Beer", "2Tap", "yellow", "\"Twice the shots from every pull. Double Tap Root Beer!\""),
        Make("04", "deadshot", "Deadshot Daiquiri", "Deadshot", "black", "\"Keep your eye on the target. Deadshot Daiquiri!\""),
        Make("05", "stamin", "Stamin-Up", "Stamin", "orange", "\"Run further, run faster. Stamin-Up!\""),
        Make("06", "revive", "Quick Revive", "Revive", "blue", "\"Back on your feet in no time. Quick Revive!\""),
        Make("07", "mule", "Mule Kick", "Mule", "green", "\"Room for one more in your arsenal. Mule Kick!\""),
        Make("08", "widow", "Widow's Wine", "Widow", "violet", "\"A bite for anyone who gets too close. Widow's Wine!\""),
        Make("09", "phd", "PHD Flopper", "PHD", "violet", "\"Dive in and blow them away. PHD Flopper!\""),
    ];

    public static IEnumerable<MongoId> AllBottles => Perks.Select(p => p.Bottle);

    // Vanilla: the Pevko beer bottle (a glass bottle the bottles are cloned from, model too until ours exists)
    public static readonly MongoId Beer = new("62a09f32621468534a797acb");
    public static readonly MongoId Roubles = new("5449016a4bdc2d6f028b456f");
    public static readonly MongoId Therapist = new("54cb57776803fa99248b456e");

    public const string DrinkParent = "5448e8d64bdc2dce718b4568";

    // Bundle keys: paths under the mod's bundles folder (bundles.json), built by build_bundle.ps1
    public const string BundleFolder = "PerkBeverages";
    public static string LootBundle(string perk) => $"item_perk_bottle_{perk}_loot.bundle";
    public static string HandsBundle(string perk) => $"item_perk_bottle_{perk}_container.bundle";
    public const string HandbookDrinks = "5b47574386f77428ca22b335";
}

/// <summary>
/// Adds the perk bottles. Their only source is the zombies mode's perk machines: they're kept out of random loot, bot gear, quest and daily
/// rewards and the flea market, as the Pack-a-Punch rocks are. Items must exist before profiles load, hence Preload.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 1)]
public class PerkItemLoader(
    ISptLogger<PerkItemLoader> logger,
    CustomItemService customItemService,
    TemplateTable templateTable,
    ItemConfig itemConfig,
    RagfairConfig ragfairConfig,
    ItemFilterService itemFilterService) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        PerkConfig config = PerkConfig.Load(m => logger.Error(m));
        var added = new List<string>();
        foreach (PerkIds.Perk perk in PerkIds.Perks)
        {
            TemplateItem? item = Clone(perk.Bottle, perk.ItemName, config.BottlePrice, perk.Name, perk.ShortName, perk.Description);
            if (item?.Properties == null)
                continue;
            Bottle(item.Properties, perk.Background);
            UseOwnModels(item.Properties, perk.Id);
            added.Add(perk.Name);
        }
        KeepOutOfTheWorld(PerkIds.AllBottles);
        logger.Success($"[PerkBeverages] Items added: {string.Join(", ", added)}");
        return Task.CompletedTask;
    }

    /// <summary>A drink like the beer it's cloned from, minus the alcohol: a little water, no other effect (yet).</summary>
    private static void Bottle(TemplateItemProperties props, string background)
    {
        props.EffectsHealth = new Dictionary<HealthFactor, EffectsHealthProperties>
        {
            [HealthFactor.Hydration] = new() { Value = 10 },
        };
        props.EffectsDamage = new Dictionary<DamageEffectType, EffectsDamageProperties>();
        props.StimulatorBuffs = "";
        props.MaxResource = 1;
        // EFT keeps the drink going until its effect's timer ends. The game reads this as whole seconds (0.3 became 0 and
        // the drink never finished): the client mod times our bottles itself, in fractions (F12 "Bottle: time at mouth")
        props.FoodUseTime = 1;
        props.BackgroundColor = background;
        props.CanSellOnRagfair = false;
    }

    /// <summary>
    /// The the original bottle: its world model (ground, inventory picture) and the hands model with the original's drink. Without the
    /// bundles the clone keeps the beer's models, so the item still works while they're being built.
    /// </summary>
    private void UseOwnModels(TemplateItemProperties props, string perk)
    {
        string dir = Path.Combine(Path.GetDirectoryName(typeof(PerkItemLoader).Assembly.Location)!, "bundles", PerkIds.BundleFolder);
        if (!File.Exists(Path.Combine(dir, PerkIds.LootBundle(perk))) || !File.Exists(Path.Combine(dir, PerkIds.HandsBundle(perk))))
        {
            logger.Warning($"[PerkBeverages] Bottle bundles missing in {dir}; using the beer's models");
            return;
        }
        props.Prefab = new Prefab { Path = $"{PerkIds.BundleFolder}/{PerkIds.LootBundle(perk)}", Rcid = "" };
        props.UsePrefab = new Prefab { Path = $"{PerkIds.BundleFolder}/{PerkIds.HandsBundle(perk)}", Rcid = "" };
    }

    /// <summary>
    /// Zombies mode only: out of random loot, bot gear, quest and daily rewards, and the flea (no trader sells them).
    /// </summary>
    private void KeepOutOfTheWorld(IEnumerable<MongoId> ids)
    {
        List<MongoId> all = ids.ToList();
        foreach (MongoId id in all)
        {
            itemConfig.Blacklist.Add(id);
            itemConfig.LootableItemBlacklist.Add(id);
            itemConfig.RewardItemBlacklist.Add(id);
            ragfairConfig.Dynamic.Blacklist.Custom.Add(id);
        }
        itemFilterService.AddItemToBlacklistCache(all);
        itemFilterService.AddItemToLootableBlacklistCache(all);
    }

    private TemplateItem? Clone(MongoId id, string name, int price, string locName, string shortName, string description)
    {
        CreateItemResult result = customItemService.CreateItemFromClone(new NewItemFromCloneDetails
        {
            ItemTplToClone = PerkIds.Beer,
            ParentId = PerkIds.DrinkParent,
            NewId = id,
            NewItemName = name,
            AddToHandbook = true,
            HandbookParentId = PerkIds.HandbookDrinks,
            HandbookPriceRoubles = price,
            AddToFleaPriceDb = false, // never on the flea
            AddToWeaponShelf = false,
            Locales = new Dictionary<string, LocaleDetails>
            {
                ["en"] = new() { Name = locName, ShortName = shortName, Description = description },
            },
        });
        if (result.Success != true || !templateTable.Items.TryGetValue(id, out TemplateItem? item))
        {
            logger.Error($"[PerkBeverages] Could not add {name}: {string.Join("; ", result.Errors ?? [])}");
            return null;
        }
        return item;
    }
}

/// <summary>
/// The bottles belong to the Kennerz Zombies mode only (its perk machines): nobody sells them (no trader, no flea) and
/// none may stay in a normal profile. Every server start takes any bottle out of every profile (stash, mail, insurance
/// returns), with anything inside it, so a profile never holds one at rest and removing the mod can't break it.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.SaveCallbacks + 1)]
public class PerkBottleSweep(ISptLogger<PerkBottleSweep> logger, SaveServer saveServer) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var bottles = new HashSet<MongoId>(PerkIds.AllBottles);
        foreach (var (id, profile) in saveServer.GetProfiles())
        {
            try
            {
                int removed = Sweep(profile.CharacterData?.PmcData?.Inventory?.Items, bottles);
                foreach (var dialogue in profile.DialogueRecords?.Values ?? Enumerable.Empty<Dialogue>())
                    foreach (var message in dialogue?.Messages ?? [])
                        removed += Sweep(message?.Items?.Data, bottles);
                foreach (var package in profile.InsuranceList ?? [])
                    removed += Sweep(package?.Items, bottles);
                if (removed > 0)
                    logger.Info($"[PerkBeverages] Profile {id}: {removed} perk bottle item(s) removed (zombies mode only)");
            }
            catch (Exception e)
            {
                logger.Error($"[PerkBeverages] Removing perk bottles from profile {id} failed: {e.Message}");
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>Takes the bottles (and anything parented to them) out of an item list; returns how many items went.</summary>
    private static int Sweep(List<Item>? items, HashSet<MongoId> bottles)
    {
        if (items == null || items.Count == 0)
            return 0;
        var gone = new HashSet<string>(items.Where(i => bottles.Contains(i.Template)).Select(i => i.Id.ToString()));
        if (gone.Count == 0)
            return 0;
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (Item item in items)
                if (item.ParentId != null && gone.Contains(item.ParentId) && gone.Add(item.Id.ToString()))
                    grew = true;
        }
        return items.RemoveAll(i => gone.Contains(i.Id.ToString()));
    }
}
