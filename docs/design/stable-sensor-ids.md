# Stable hardware sensor identity

Status: approved design with scope amendments (2026-09-15). The reviewer's scope
decisions below OVERRIDE the corresponding parts of the original design that follows.

## Scope decisions (binding)

1. **ID grammar**: keep `hwmon/v1/<chip>/<anchor>[~<secondary>]/<channel>` and
   `thermal/v1/<anchor>[~<secondary>]/temp` exactly as specified, including
   percent-encoding. DROP the `!<hash>` token shortening: tokens are used verbatim
   after percent-encoding (sysfs identity strings are short). Compare ordinally.
2. **No pinned directory handles / openat.** Cached channel paths
   (`/sys/class/hwmon/hwmonN/temp1_input`) are re-derived at every rescan (10 s).
   A path reused by a different kernel object within one rescan window is an
   accepted limitation; a rescan also compares the resolved realpath of each
   hwmon entry and treats a change as a replacement.
3. **Graph history**: key hardware history queues by the canonical live id.
   Unresolved items get no queue and enqueue nothing. DROP the "availability
   incarnation" concept and the GraphHistoryStore extraction; when a descriptor
   disappears from the catalog, drop its queue.
4. **XML metadata**: add ONE optional element `HardwareSensorIdentity` with
   exactly three optional children: `ChipName`, `ChannelLabel`, `OriginalId`.
   No `Version`/`Anchor` children (the anchor is already inside the id).
   Omitted when null. Deep-copied on clone.
5. **Migration on load** as designed: migrate in memory, do not write; persisted
   on the next normal save. Unresolved ids are kept verbatim.
6. **Polling loop**: one serialized worker (poll + rescan every 10 s) replacing
   the overlapping `System.Timers.Timer` callbacks, as designed. Keep the
   existing first-poll "force poll all" behaviour.
7. **Ambiguity**: as designed — identical full identity tuples are excluded from
   binding and logged; no ordinals ever derived from enumeration order.
8. **Legacy migration evidence rule**: as designed — a legacy `hwmonN/…` id migrates
   only when this boot's `hwmonN` chip, the channel, and (when present) the item's
   `SensorName` label agree on exactly one catalog descriptor; with no label hint,
   accept only if that channel name exists on exactly one chip in the catalog.
9. **`system/...` keys** (disk, network, gpu, amdgpu, igpu): untouched; documented
   as a follow-up.
10. **Tests**: new xunit project `tests/InfoPanel.Sensors.Linux.Tests` with the fake
    sysfs root fixture; Core resolver/migration tests in `tests/InfoPanel.Core.Tests`.
    Existing golden-file fixture and its `hwmon4/curr5` expectation stay unchanged.
11. **Inspector**: show a one-line status ("unresolved: <id>") for unresolved hardware
    bindings; no other UI redesign. Replace Sensor stays the repair action.
12. **Export**: export migrates the copy it writes to the archive using the same
    migration hook; it does not save the source profile.

---

# Original design (Codex, gpt-6-astra, 2026-09-15)

Publish readings under those IDs and share one immutable catalog between polling, resolution, demand tracking, and the UI.
Migrate legacy bindings only when the available evidence identifies a single sensor; preserve unresolved IDs for later recovery.
Rescan every 10 seconds, derive identity only during scans, and prevent removed devices from leaving stale readings or graph history.
This document proposes changes only; no repository files were modified and no build or tests were run.

## 2. Stable id grammar and anchor derivation algorithm

### Identity contract

An ID identifies a driver-defined channel on a particular chip or thermal zone. It must survive changes to `hwmonN`, `thermal_zoneN`, NVMe controller numbering, SCSI host numbering, and I²C logical bus numbering.

The guarantee applies while the identifying hardware attributes and driver channel definitions remain available. Physical relocation, firmware changes, and hardware that exposes no distinguishing identity require explicit fallback rules.

Kernel channel names such as `temp1`, `fan2`, and `in0` remain unchanged. Their numbering and units follow the hwmon ABI; labels remain descriptive metadata rather than part of the normal channel identity. [Linux hwmon ABI](https://docs.kernel.org/hwmon/sysfs-interface.html)

### Exact grammar

```ebnf
hwmon-id   = "hwmon/v1/", token, "/", anchor, [ "~", secondary ], "/", channel ;
thermal-id = "thermal/v1/", anchor, [ "~", secondary ], "/temp" ;

anchor     = anchor-kind, "+", token, { "+", token } ;
secondary  = secondary-kind, "+", token, { "+", token } ;

anchor-kind =
    "nvme-serial" | "block-wwid" | "block-serial" |
    "usb-serial" | "usb-port" | "pci" | "platform" |
    "type" | "i2c" | "name" ;

secondary-kind =
    "pci" | "acpi" | "of" | "port" | "role" | "adapter" | "meta" ;

channel = ("temp" | "fan" | "in" | "curr" |
           "power" | "freq" | "humidity"), decimal-index ;

decimal-index = "0" | nonzero-digit, { digit } ;

token = escaped-token | shortened-token ;
escaped-token = token-unit, { token-unit } ;
token-unit = ASCII-letter | digit | "." | "_" | "-" | "%", HEX, HEX ;
shortened-token = escaped-token, "!", 32-lowercase-hex-digits ;
```

The token preceding the hwmon anchor is the normalized chip name. Thermal IDs have no separate chip-name component: their `type` anchor supplies it.

**Encoding rules:**

- Trim surrounding whitespace from sysfs strings. Preserve case except for attributes with explicitly defined normalization below.
- Encode UTF-8 bytes outside `[A-Za-z0-9._-]` as uppercase `%HH`. Encode literal `%`, `+`, `~`, `!`, `/`, and `\`.
- Escape tokens equal to `.` or `..`; escape a trailing dot and Windows reserved device basenames.
- If an encoded token exceeds 64 characters, use its longest complete encoded prefix of at most 24 characters, followed by `!` and the first 128 bits of SHA-256 of the complete normalized UTF-8 value.
- Compare complete IDs using `StringComparer.Ordinal`.
- Retain full, unshortened identity values in catalog metadata. Detect shortened-token collisions and report ambiguity rather than overwriting a reading.
- `/` separates logical components. An ID is not interpreted as an arbitrary filesystem path.

Most IDs remain approximately 40–100 characters. The encoding is XML-safe and its individual path components are filesystem-safe.

### Discovery and normalization

Perform these operations only during a scan:

1. Enumerate `class/hwmon/hwmon*` and `class/thermal/thermal_zone*` below the injected sysfs root.
2. Resolve class symlinks to their real directories.
3. Discover the owning device and relevant ancestors by subsystem and attributes. A `device` link can help locate the owner but is not required.
4. Read identity attributes, chip name, thermal type, channel filenames, optional chip label, and channel labels.
5. Derive the primary anchor and stable secondary discriminator.
6. Construct descriptors independently of whether a value file currently returns a valid reading.

Do not assume that the USB identity is exactly `../../idVendor`, or that a PCI function occurs at a fixed depth. Kernel guidance explicitly discourages depending on fixed parent positions or the existence of `device` links. [Sysfs access rules](https://docs.kernel.org/admin-guide/sysfs-rules.html)

### Anchor priority

This is an ordered selection among **eligible anchors for the actual sensor owner**. An enclosing bus controller is not automatically eligible as the identity of every child device.

| Priority | Owner and required evidence | Primary anchor | Important restrictions |
|---|---|---|---|
| 1 | NVMe controller with a nonempty serial | `nvme-serial+<serial>` | Find the actual controller ancestor; read its `serial`, including through the corresponding resolved `class/nvme` entry. Never persist `nvme0` or `nvme1`. |
| 1 | `drivetemp` or another identifiable block-backed owner | `block-wwid+<wwid>`, otherwise `block-serial+<serial>` | Associate the hwmon owner with its block device first. Inspect block `wwid`, device `wwid`, then device `serial`. Do not select an unrelated disk by enumeration order. |
| 2 | USB device with VID, PID, and usable serial | `usb-serial+<vid>+<pid>+<serial>` | VID/PID are four lowercase hexadecimal digits. Read the USB device’s attributes, not those of a hub ancestor. |
| 3 | USB device without usable serial | `usb-port+<vid>+<pid>+<host-anchor>+<port-chain>` | Weaker, location-based identity. Use the host’s stable PCI/platform identity and downstream port chain; discard USB bus and device numbers. |
| 4 | PCI function that owns the sensor | `pci+<dddd-bb-dd.f>` | Use the nearest relevant endpoint, including the PCI domain. For the example NIC this is `0000-ab-00.0`, not its upstream bridge. |
| 5 | Platform device that owns the sensor | `platform+<device-name>` | Preserve meaningful suffixes such as `coretemp.0` and `nct6775.656`. Do not strip all numbers. |
| 6 | Thermal-zone-backed hwmon or direct thermal zone | `type+<normalized-type>` | Read `type`; never substitute `thermal_zoneN`. Add firmware identity as a secondary discriminator when exposed. |
| 7 | I²C client | `i2c+<adapter-name>+<address>` | Use the adapter’s normalized name and four-digit hexadecimal client address. Add stable adapter topology and mux route as a secondary discriminator. |
| 8 | No better identity | `name+<normalized-chip-name>` | Weak identity; bindable only when distinguishable under the collision rules below. |

An I²C client beneath a PCI SMBus controller uses the I²C rule; the PCI controller alone does not identify the client. Likewise, a SATA disk does not become identified merely by its host controller’s PCI address.

For NVMe, controller serial is deliberately preferred over namespace WWID for controller-level hwmon temperatures. Namespace identity belongs to a different object and can change when namespaces are reconfigured. The kernel exposes controller serial and namespace WWID separately. [NVMe sysfs ABI](https://raw.githubusercontent.com/torvalds/linux/master/Documentation/ABI/stable/sysfs-nvme)

For I²C muxes, reconstruct the route from the root adapter through mux client addresses and mux channel numbers. Do not preserve logical bus numbers embedded in generated adapter names. Normalize only recognized generated forms; retain ordinary adapter names. [Linux I²C sysfs topology](https://docs.kernel.org/i2c/i2c-sysfs.html)

### Stable secondary discriminator and duplicate anchors

Use a **structural discriminator**, rather than an ordinal assigned among currently present devices.

Select the discriminator by owner kind:

| Owner | Secondary discriminator |
|---|---|
| NVMe | PCI endpoint, when present: `pci+0000-81-00.0`. |
| USB | Stable host/port topology and interface or functional role. |
| PCI child such as an MDIO PHY | Stable child address or role, for example `role+mdio+00`. |
| Platform chip | Stable child role, firmware identity, or documented device label when necessary to distinguish registrations beneath the same owner. |
| Thermal zone | ACPI namespace path or device-tree node path, when exposed. |
| I²C | Root adapter’s stable owner identity and mux address/channel route. |
| Block-backed sensor | Stable transport identity when available without SCSI host/target numbering. |
| Name-only fallback | A documented chip label, or a sorted signature of channel names and labels as a last, explicitly weak discriminator. |

**Include the applicable discriminator whenever it is available, even when the primary anchor is currently unique.** Consequently:

- Two NVMe controllers with identical serials but different PCI endpoints already have different IDs.
- Removing one duplicate does not shorten or rename the other.
- Adding a duplicate does not change an existing ID.
- Directory enumeration order never affects identity.

Sort descriptors by the full normalized tuple `(source, chip, primary anchor, secondary discriminator, channel)` using ordinal comparison. This also provides deterministic UI and dump ordering.

If two distinct devices still have identical complete identity tuples, the available information cannot distinguish them. Mark the conflicting descriptors `AmbiguousIdentity`, expose their current paths in diagnostics, and exclude them from bindable stable-ID lookup and `SENSORHASH`. Never select the first device or use `hwmonN`, inode numbers, current temperature, or discovery time to manufacture a persistent identity.

A name-only device with no discriminator is usable while unique, with a visible weak-identity classification. If it becomes ambiguous, its existing binding becomes unresolved.

### IDs for this machine

Read-only inspection confirmed the supplied topology and additionally found:

- PCI `0000:81:00.0`: NVMe serial `253652BEC1AD`.
- PCI `0000:01:00.0`: NVMe serial `233529800995`.
- `thermal_zone0`: type `acpitz`, ACPI path `\_TZ_.TZ00`.
- `thermal_zone5`: type `iwlwifi_1`.

The following show a representative `temp1` channel. Other existing channels retain their actual names.

| Current entry | Proposed stable ID |
|---|---|
| `hwmon0`, `acpitz` | `hwmon/v1/acpitz/type+acpitz~acpi+%5C_TZ_.TZ00/temp1` |
| `hwmon1`, `r8169_0_ab00:00` | `hwmon/v1/r8169/pci+0000-ab-00.0~role+mdio+00/temp1` |
| `hwmon2`, NVMe at `81:00.0` | `hwmon/v1/nvme/nvme-serial+253652BEC1AD~pci+0000-81-00.0/temp1` |
| `hwmon3`, NVMe at `01:00.0` | `hwmon/v1/nvme/nvme-serial+233529800995~pci+0000-01-00.0/temp1` |
| `hwmon4`, `wireview` | `hwmon/v1/wireview/platform+wireview_hwmon/temp1` |
| `hwmon5`, `coretemp` | `hwmon/v1/coretemp/platform+coretemp.0/temp1` |
| `hwmon6`, `iwlwifi_1` | `hwmon/v1/iwlwifi/type+iwlwifi/temp1` |

For the golden-file binding, the corresponding WireView ID is:

```text
hwmon/v1/wireview/platform+wireview_hwmon/curr5
```

The direct thermal-zone IDs are:

| Current entry | Proposed stable ID |
|---|---|
| `thermal/thermal_zone0` | `thermal/v1/type+acpitz~acpi+%5C_TZ_.TZ00/temp` |
| `thermal/thermal_zone1` | `thermal/v1/type+INT3400%20Thermal/temp` |
| `thermal/thermal_zone2` | `thermal/v1/type+TCPU/temp` |
| `thermal/thermal_zone3` | `thermal/v1/type+TCPU_PCI/temp` |
| `thermal/thermal_zone4` | `thermal/v1/type+x86_pkg_temp/temp` |
| `thermal/thermal_zone5` | `thermal/v1/type+iwlwifi/temp` |

Two explicit normalization rules apply here:

- Recognized r8169 PHY name forms become chip name `r8169`; PCI address and PHY address carry the identity separately.
- Recognized `iwlwifi_<decimal>` chip/type names become `iwlwifi`. The kernel constructs that suffix from an incrementing counter, so retaining it would preserve enumeration dependence. Multiple indistinguishable iwlwifi zones must remain ambiguous unless an owner relationship is exposed. [iwlwifi thermal registration](https://raw.githubusercontent.com/torvalds/linux/master/drivers/net/wireless/intel/iwlwifi/mvm/tt.c)

### Additional edge cases

- **k10temp/zenpower:** use the actual PCI or platform owner. Treat the two driver names as distinct; do not assume their channel semantics are interchangeable.
- **nct6775/it87:** retain platform instance/address suffixes such as `nct6775.656`.
- **amdgpu/nouveau/i915:** use the owning PCI function; ignore DRM `cardN`.
- **No `device` link:** inspect the resolved hwmon directory’s actual ancestry. Only use name fallback if no supported identity is discoverable.
- **Missing/empty identity attributes:** fall through the priority table. Reject documented placeholder serials such as an empty string or “unknown”; classify suspicious duplicated serials through normal collision handling.
- **Missing channel labels:** display the channel name and mark the label as synthetic. It supplies no additional migration evidence.
- **Generic thermal-backed hwmon aggregation:** the thermal subsystem can aggregate zones of the same type into one hwmon device. Its `tempN` association must not be inferred from zone numbering. Publish a bindable hwmon channel only when its zone association is unambiguous; otherwise expose the direct thermal-zone sensors and retain the aggregate entry for diagnostics only. This is an exception to assuming ordinary driver channel stability. [Thermal sysfs interface](https://docs.kernel.org/driver-api/thermal/sysfs-api.html)

## 3. Resolver design

### Ownership and data structures

Put platform-neutral contracts, parsing, and resolution in Core. Put sysfs discovery and identity derivation in `InfoPanel.Sensors.Linux`. Core must not reference the Linux project.

Introduce these concepts:

| Type | Contents and purpose |
|---|---|
| `SensorDescriptor` | Stable ID, chip key, raw/normalized chip names, channel, raw/display label, label provenance, unit/category, complete identity tuple, identity strength, current legacy aliases, availability incarnation. |
| `SensorCatalogSnapshot` | Generation, immutable ordered descriptors, stable-ID index, legacy-alias multimap, fuzzy indexes, strong-anchor index, ambiguity records. |
| `HwmonPollPlan` | Linux-only value-reader handles, stable output keys, units and divisors. No model or UI references. |
| `SensorReference` | Source type, persisted ID, `SensorName`, optional persisted identity hints. A value-type lookup request. |
| `SensorResolution` | Status, canonical live key, descriptor, catalog generation, match reason, whether persistent rewriting is supported. |
| `SensorIdResolver` | Current immutable catalog snapshot and generation-specific positive/negative resolution cache. |
| `HardwareSensorIdentity` | Optional XML metadata: version, chip name, original channel label, complete anchor/discriminator, and original ID for a migrated binding. |

Use frozen dictionaries/sets and immutable arrays for published catalog and demand data. A legacy alias maps to candidates, not necessarily one result.

The resolution cache key must include the entire reference, particularly label and identity hints. Two profiles can contain the same obsolete `hwmon2/temp1` but refer to different intended sensors.

### Fallback chain

For `SensorType.Hwmon`, resolve as follows:

1. **Exact stable-ID match.**  
   Find the descriptor in the current catalog. An ambiguous descriptor is not an exact successful match. A present descriptor with no valid reading remains correctly bound; reading failure does not trigger rebinding.

2. **Legacy migration candidate.**  
   Recognize only these legacy forms:

   ```text
   ^hwmon[0-9]+/(temp|fan|in|curr|power|freq|humidity)[0-9]+$
   ^thermal/thermal_zone[0-9]+$
   ```

   Look up what that path means on this boot. Check its channel, available chip hint, and label against the item.

   The current index is a candidate-generation hint, not proof of identity. Accept only when the available descriptive evidence selects one sensor across the relevant catalog. For example:

   - `hwmon4/curr5`, label `Pin 5`, with one matching candidate: migrate.
   - `hwmon2/temp1`, label `Composite`, with two matching NVMe chips: unresolved.
   - A label contradicting this boot’s indexed sensor: reject that candidate and continue to fuzzy matching.
   - With no meaningful chip or label hint, accept only if that channel has exactly one candidate in the relevant namespace; classify and log this as low-confidence legacy migration.

3. **Fuzzy match by chip, channel, and label.**  
   Use indexed intersections rather than scanning all sensors:

   - Match the channel exactly.
   - Match normalized chip name when known.
   - Match the saved hardware label; for old files, use `SensorName` as the label hint.
   - Normalize label comparison by trimming, collapsing whitespace, and ordinal case-insensitive comparison.
   - Synthetic labels such as `temp1` are not additional evidence.
   - Do not infer identity from the display item’s editable `Name`.
   - Require exactly one compatible candidate. No edit-distance matching or “first closest” selection.

   For an already stable ID, constrain candidates by the identity encoded in the ID or saved metadata. A missing NVMe serial must never match a different drive merely because both labels say `Composite`.

   A changed secondary location may resolve when the strong primary identity—such as NVMe or USB serial—is unchanged and uniquely identifies a compatible chip/channel. This permits recovery after a device moves. Conflicting strong identities always reject the match.

4. **Unresolved.**  
   Return no live key and no reading. Keep the persisted ID intact.

`system/...` IDs use exact pass-through lookup under `SensorType.Hwmon`; they are outside this migration. Plugin IDs retain their existing source. `Libre` and `HwInfo` Windows bindings remain untouched and continue to return no Linux hardware reading unless explicitly rebound.

This last distinction corrects the current mismatch between `SensorDemand`’s comment and the display models: their `GetValue()` implementations currently read hardware only for `SensorType.Hwmon`.

### Read and demand seams

Keep the existing string overload for stable/system callers, and add reference-aware operations:

```csharp
SensorResolution ResolveHwmonSensor(SensorReference reference);
SensorReading? ReadHwmonSensor(SensorReference reference);
SensorReading? ReadHwmonSensor(string canonicalId);
```

The five sensor-bearing model families call the reference-aware overload. It resolves entirely in memory and then reads `SENSORHASH[canonicalId]`.

`SensorDemand.Collect` uses the same resolver and records **canonical live keys**, never unresolved legacy strings. `IsHwmonUsed` remains a cheap set-membership test against those canonical keys.

Demand snapshots carry the catalog generation used during collection. On a catalog change:

- Invalidate the demand snapshot immediately.
- Rebuild before the next demand-gated poll, bypassing the normal one-second throttle.
- If collection fails or its generation is outdated, temporarily poll all hwmon descriptors until a valid snapshot is available. Do not silently starve newly resolved sensors.
- Include hotkey-required plugin IDs before freezing the snapshot; `AppHost` currently mutates that set after collection.

### Thread safety and invalidation

- A single monitor worker owns polling, scan publication, reading removal, and polling-handle disposal. Replace overlapping `System.Timers.Timer` callbacks with a serialized loop.
- Build a candidate catalog off the UI thread. Atomically publish one snapshot reference with `Interlocked.Exchange`; readers acquire it with `Volatile.Read`.
- Each resolve call uses one captured snapshot. Publication never mutates an existing snapshot.
- Cache resolution results per snapshot. Replacing the snapshot invalidates both successful and unresolved results.
- Advance the generation when descriptors, aliases, identity metadata, channel metadata, or polling locations change. Identical scans do not advance it.
- UI updates and live model migrations execute through the host’s UI/owner-thread dispatch seam.
- Polling never edits display items.
- Use immutable profile-list snapshots in `AppHost`’s demand provider and `GroupDisplayItem.DisplayItemsCopy` during recursive traversal; do not enumerate mutable UI collections from the polling worker.

Keep `SENSORHASH` as the existing concurrent dictionary, with **stable keys for hwmon/thermal and existing keys for `system/...`**. No duplicate legacy entries are published.

During catalog replacement, remove readings for departed/replaced descriptors before publishing the replacement catalog. The serialized worker prevents an old poll from repopulating them. The read seam also checks catalog membership and rechecks the snapshot if publication occurred during lookup.

### History and missing-value behavior

`GraphDraw` resolves the item before choosing a queue. Key hardware history by:

```text
(canonical live ID, availability incarnation)
```

An incarnation changes when a sensor disappears and returns, or its actual endpoint is replaced. It does not change merely because an unrelated sensor appears.

- Legacy and stable references to the same live sensor share history.
- Rewriting a legacy ID to that same canonical ID preserves history.
- Removal, rebinding to another sensor, or replacement cannot reuse another sensor’s samples.
- An unresolved item does not acquire a queue under its unresolved string.
- Clear item-specific smoothing and auto-range state when its resolved history key changes.
- Remove abandoned history entries; retain existing queue synchronization and use `GetOrAdd` for creation.

Missing data must remain visibly missing:

- Sensor text retains its current `"-"` behavior.
- Graphs render no live trace while unresolved.
- Bars/donuts do not substitute zero for an unresolved reading.
- Bound gauges return no value frame; their current first-image fallback must not imply a valid minimum reading.
- Sensor images remain hidden; HTTP sensor images return no sensor-provided image.

### Diagnostics

Log one unresolved warning per `(profile GUID, item GUID, persisted ID)` per application session, after the initial catalog is ready. Do not log per frame, demand rebuild, or identical rescan.

Log successful migration with old ID, new ID, and reason. Log recovery when a previously unresolved binding resolves. Expose status and identity strength in the inspector, and raw paths/legacy aliases in verbose sensor dumps.

## 4. Integration points: files to change

Paths below are relative to `/home/emanuele/infopanel-v2`. New paths describe proposed files.

| File | Precise change |
|---|---|
| `src/InfoPanel.Core/Sensors/SensorId.cs` — new | Implement the versioned grammar, encoding, parsing, channel validation, and identity-key comparison. |
| `src/InfoPanel.Core/Sensors/SensorCatalog.cs` — new | Define immutable descriptors, references, resolution results, catalog snapshots, and resolver contract. No sysfs dependencies. |
| `src/InfoPanel.Core/Sensors/SensorIdResolver.cs` — new | Implement indexed exact/legacy/fuzzy resolution, ambiguity handling, strong-identity guards, and generation-specific caching. |
| `src/InfoPanel.Core/Models/HardwareSensorIdentity.cs` — new | Define the optional XML metadata DTO and deep-copy support. Keep runtime catalog/cache state out of XML. |
| `src/InfoPanel.Core/Models/SensorBinding.cs` — new | Provide a public Core facade for capturing/applying a complete binding across internal `ISensorItem` implementations, including hints and original ID. Support one undoable binding operation. |
| [ISensorItem.cs](/home/emanuele/infopanel-v2/src/InfoPanel.Core/Models/ISensorItem.cs) | Extend the internal hardware-binding contract with optional identity metadata. |
| [SensorReader.cs](/home/emanuele/infopanel-v2/src/InfoPanel.Core/Models/SensorReader.cs) | Register a resolver alongside the source; expose reference-aware resolution/read methods and retain exact canonical-string reads. |
| [SensorDemand.cs](/home/emanuele/infopanel-v2/src/InfoPanel.Core/Models/SensorDemand.cs) | Resolve references during collection; publish immutable sets with catalog generation; add explicit invalidation; traverse group snapshots; preserve plugin demand. |
| [SensorDisplayItem.cs](/home/emanuele/infopanel-v2/src/InfoPanel.Core/Models/SensorDisplayItem.cs) | Add optional metadata and pass the full reference to `SensorReader`; preserve `LibreSensorId` and existing text behavior. |
| [GaugeDisplayItem.cs](/home/emanuele/infopanel-v2/src/InfoPanel.Core/Models/GaugeDisplayItem.cs) | Add metadata/reference-aware reads; deep-copy hints on cloning; return no frame for a bound but unavailable sensor. |
| [ChartDisplayItem.cs](/home/emanuele/infopanel-v2/src/InfoPanel.Core/Models/ChartDisplayItem.cs) | Add metadata/reference-aware reads for graph, bar, and donut subclasses; deep-copy hints in their clone paths. |
| [SensorImageDisplayItem.cs](/home/emanuele/infopanel-v2/src/InfoPanel.Core/Models/SensorImageDisplayItem.cs) | Add metadata/reference-aware reads; preserve hidden-on-missing behavior and independent metadata when cloned. |
| [HttpImageDisplayItem.cs](/home/emanuele/infopanel-v2/src/InfoPanel.Core/Models/HttpImageDisplayItem.cs) | Add metadata/reference-aware reads; preserve null sensor URL/image behavior and independent metadata when cloned. |
| [GroupDisplayItem.cs](/home/emanuele/infopanel-v2/src/InfoPanel.Core/Models/GroupDisplayItem.cs) | Publish the existing immutable child snapshot with explicit memory visibility so demand and migration traversal do not copy mutable collections off-thread. |
| `src/InfoPanel.Core/Persistence/SensorBindingMigration.cs` — new | Recursively migrate eligible hardware bindings using an injected resolver; return a report of changes/unresolved items without saving files. |
| [ConfigPersistence.cs](/home/emanuele/infopanel-v2/src/InfoPanel.Core/Persistence/ConfigPersistence.cs) | Add an optional migration service hook after deserialization/profile attachment. Apply it consistently to ordinary loads and backup loads. Default remains no migration when no service is configured. |
| [ProfileTransfer.cs](/home/emanuele/infopanel-v2/src/InfoPanel.Core/Persistence/ProfileTransfer.cs) | Serialize a current, detached item snapshot on export instead of blindly copying stale disk XML. Route imported item loading through the same migration path. Preserve archive structure. |
| [DisplayItemStore.cs](/home/emanuele/infopanel-v2/src/InfoPanel.Core/Stores/DisplayItemStore.cs) | Reconcile already-loaded bindings after catalog changes on the owner thread; suppress migration-only autosave; preserve session backups; capture save/export snapshots after reconciliation. Recursively hook loaded group children. |
| `src/InfoPanel.Sensors.Linux/SysfsAccess.cs` — new | Define injected sysfs paths and metadata/value access abstractions; implement symlink resolution, bounded reads, pinned directory handles, and production value readers. |
| `src/InfoPanel.Sensors.Linux/HwmonIdentity.cs` — new | Implement owner classification, anchor priority, known generated-name normalization, stable secondary discriminators, and collision detection. |
| `src/InfoPanel.Sensors.Linux/HwmonCatalogScanner.cs` — new | Enumerate hwmon/thermal descriptors and channels once per scan; build immutable catalog candidates and Linux polling plans. |
| [HwmonMonitor.cs](/home/emanuele/infopanel-v2/src/InfoPanel.Sensors.Linux/HwmonMonitor.cs) | Replace per-poll enumeration with cached descriptors; publish stable keys; serialize polling/scanning; remove stale readings; expose catalog generation. Make `GetOrderedList()` use catalog metadata and append existing system-provider metadata. Extend `HwmonSensorInfo` with optional chip key/identity/status fields. |
| [AppHost.cs](/home/emanuele/infopanel-v2/src/InfoPanel.App/AppHost.cs) | Own/wire the catalog, resolver, migration hook, and source; establish startup ordering; invalidate demand and reconcile loaded items after publication; expose immutable profile membership to demand collection; reset registrations on shutdown. |
| [HeadlessRunner.cs](/home/emanuele/infopanel-v2/src/InfoPanel.App/HeadlessRunner.cs) | Print canonical IDs and catalog labels, including unavailable/ambiguous diagnostic entries. With `--verbose`, include legacy alias, identity strength, and current sysfs location. Keep force-poll behavior. |
| [SensorTreeViewModel.cs](/home/emanuele/infopanel-v2/src/InfoPanel.App/ViewModels/SensorTreeViewModel.cs) | Group hwmon entries by chip key, not chip name alone; retain metadata on leaves; refresh topology on catalog-generation change; preserve selection/expansion by stable keys; clear values when readings disappear. |
| [DesignerPage.axaml.cs](/home/emanuele/infopanel-v2/src/InfoPanel.App/Views/Pages/DesignerPage.axaml.cs) | Make `BindSensorFields`, `CreateSensorItem`, drag/drop, and Replace Sensor use complete stable bindings. Include hints in undo/redo. Refresh the tree on generation changes and preserve active search filtering. |
| [InspectorPanel.cs](/home/emanuele/infopanel-v2/src/InfoPanel.App/Designer/InspectorPanel.cs) | Display canonical binding, unresolved/ambiguous status, and weak identity where relevant; retain Replace Sensor as the repair action. |
| [SensorsPage.axaml.cs](/home/emanuele/infopanel-v2/src/InfoPanel.App/Views/Pages/SensorsPage.axaml.cs) | Rebuild on catalog changes, reapply search, and distinguish discovered sensors from currently available readings in the count. |
| [DashboardPage.axaml.cs](/home/emanuele/infopanel-v2/src/InfoPanel.App/Views/Pages/DashboardPage.axaml.cs) | Pass the current store snapshot to export so pending migrations and current edits are represented. |
| [GraphDraw.cs](/home/emanuele/infopanel-v2/src/InfoPanel.Rendering/Drawing/GraphDraw.cs) | Resolve hardware history keys, use incarnation-aware queues, reset item smoothing/range on binding changes, and handle missing chart readings explicitly. |
| `src/InfoPanel.Rendering/Drawing/GraphHistoryStore.cs` — new | Extract testable history ownership, sampling, and eviction from `GraphDraw`; preserve queue locking. |
| [InfoPanel.Rendering.csproj](/home/emanuele/infopanel-v2/src/InfoPanel.Rendering/InfoPanel.Rendering.csproj) | Allow `InfoPanel.App.Tests` to test the internal history component. |
| `tests/InfoPanel.Sensors.Linux.Tests/InfoPanel.Sensors.Linux.Tests.csproj` — new | Add a .NET 10 xUnit project referencing the Linux sensor project, following existing package versions. |
| `tests/InfoPanel.Sensors.Linux.Tests/FakeSysfs.cs` — new | Temporary sysfs tree builder, relative symlinks, controlled enumeration ordering, injected read counters, and cleanup. |
| `tests/InfoPanel.Sensors.Linux.Tests/HwmonIdentityTests.cs` — new | Anchor, normalization, duplicate, encoding, and topology-renumbering tests. |
| `tests/InfoPanel.Sensors.Linux.Tests/HwmonMonitorTests.cs` — new | Demand gating, rescans, removal, late appearance, handle reuse protection, and hot-path read-count tests. |
| `tests/InfoPanel.Core.Tests/SensorIdResolverTests.cs` — new | Exact, legacy, fuzzy, ambiguity, contradictory identity, and cache-invalidation tests using synthetic catalogs. |
| `tests/InfoPanel.Core.Tests/SensorBindingMigrationTests.cs` — new | All item families, nested groups, original-ID retention, idempotence, and no-write-on-load tests. |
| `tests/InfoPanel.Core.Tests/SensorDemandTests.cs` — new | Canonical demand keys, generation invalidation, unresolved bindings, groups, and plugin isolation. |
| [GoldenFileTests.cs](/home/emanuele/infopanel-v2/tests/InfoPanel.Core.Tests/GoldenFileTests.cs) | Preserve existing no-migration compatibility assertions; add explicitly configured migration cases and stable-ID round trips. |
| [ProfileTransferTests.cs](/home/emanuele/infopanel-v2/tests/InfoPanel.Core.Tests/ProfileTransferTests.cs) | Test migrated export snapshots, unresolved import/export, and metadata preservation. |
| `tests/InfoPanel.App.Tests/SensorBindingTests.cs` — new | Designer creation/replacement, complete undo/redo, duplicate-chip grouping, and tree refresh. |
| `tests/InfoPanel.App.Tests/SensorRenderingTests.cs` — new | Canonical history sharing, rebinding/removal reset, and missing-value rendering behavior. |
| [DesignerSessionTests.cs](/home/emanuele/infopanel-v2/tests/InfoPanel.App.Tests/DesignerSessionTests.cs) | Add complete-binding clone/undo coverage and place persistence-mutating tests in the shared serialized test collection. |
| `tests/InfoPanel.Core.Tests/TestCollections.cs` and `tests/InfoPanel.App.Tests/TestCollections.cs` — new | Explicitly serialize tests using static persistence/source seams; provide reset fixtures. |
| [InfoPanel.slnx](/home/emanuele/infopanel-v2/InfoPanel.slnx) | Register the new Linux sensor test project. |
| [README.md](/home/emanuele/infopanel-v2/README.md) | Document durable hardware bindings, migration limitations, and dump diagnostics. |
| [USER-GUIDE.md](/home/emanuele/infopanel-v2/docs/USER-GUIDE.md) | Explain automatic recovery, missing/ambiguous sensors, replug behavior, and manual repair. |

`Profile.cs`, `SensorType.cs`, `SensorReading.cs`, and existing golden XML fixtures require no changes. Profiles do not own their display-item collections; migration belongs in display-item loading and the store.

`LinuxSystemSensors.cs`, `NvmlMonitor.cs`, `RocmSmiMonitor.cs`, and `IntelGpuMonitor.cs` retain their current key schemes in this implementation. Additional `HwmonSensorInfo` fields must have compatible defaults for these producers.

## 5. Migration and persistence plan

### Serialization contract

Retain `LibreSensorId` as the authoritative persisted binding. Do not rename it, change enum values, alter polymorphic item names, or remove entries from `DisplayItemExtraTypes`.

Add optional `HardwareSensorIdentity` metadata to the five hardware-bearing model families. Omit it when null. For example:

```xml
<LibreSensorId>hwmon/v1/wireview/platform+wireview_hwmon/curr5</LibreSensorId>
<HardwareSensorIdentity>
  <Version>1</Version>
  <ChipName>wireview</ChipName>
  <ChannelLabel>Pin 5</ChannelLabel>
  <Anchor>platform+wireview_hwmon</Anchor>
  <OriginalId>hwmon4/curr5</OriginalId>
</HardwareSensorIdentity>
```

Keep runtime descriptors, resolution status, generations, and caches out of XML. Deep-copy the metadata when duplicating items.

New designer bindings populate identity hints immediately. Their `OriginalId` is absent. Explicit Replace Sensor clears the prior migration provenance and applies the selected sensor’s complete binding.

### Load behavior

`ConfigPersistence.LoadDisplayItemsFromFile` is the common hook for ordinary loads and backup loads:

1. Deserialize with the existing serializer configuration.
2. Attach the profile using the existing recursive `SetProfile`.
3. If the resolver has completed its initial scan, recursively attempt migration.
4. Return the items without writing the file.

For a successful legacy migration:

- Store the original ID if not already preserved.
- Replace `LibreSensorId` with the canonical live ID.
- Populate identity hints.
- Leave the user’s item name and `SensorName` unchanged.
- Emit one migration log entry.

For an unresolved or ambiguous binding, leave its ID and existing metadata unchanged.

A later successful resolution may migrate it after a rescan. Do not keep retrying sysfs from the item getter; resolution retries use the new catalog.

### Startup ordering

`AppHost.Initialize` registers the empty resolver, read source, and migration hook before any possible item load.

`StartSensorsAsync` performs the initial hwmon/thermal scan and publishes the catalog before starting demand-gated hardware polling. Items loaded earlier receive a reconciliation pass through `DisplayItemStore`; later lazy loads migrate through `ConfigPersistence`.

No catalog yet means `NotReady`, not a permanent unresolved result and not a warning.

### Save behavior

Migration changes in-memory bindings immediately but does not itself initiate autosave.

- Suppress store autosave reactions while applying migration-only changes.
- Persist migrated IDs on the next normal save, autosave following an edit, profile switch save, or shutdown save.
- Preserve the existing session-start backup before the first store write.
- Reconcile on the owner thread before capturing a detached save snapshot.
- Never mutate live models from a background serializer.
- A second load/save under the same catalog must be byte-stable.

A missing device must never cause an ID to be blanked, replaced by a guessed ID, or deleted from the profile.

### Import/export

The current exporter copies `profiles/{guid}.xml`; this can miss migrations made only in memory.

Change export to serialize a detached snapshot:

- UI export passes the current store snapshot.
- Non-UI callers may omit it; the exporter loads the saved items through the configured load/migration path.
- Apply eligible migration to the detached export copy before writing `DisplayItems.xml`.
- Do not save the source profile merely to export it.

Keep `.infopanel` contents unchanged: `Profile.xml`, `DisplayItems.xml`, and assets.

Import retains its fresh GUID and inactive-profile behavior. It preserves the archive’s binding strings and uses the normal load hook when items enter the store. A profile imported without matching hardware remains unresolved and can recover later.

Old applications can still deserialize the layout and ignore added metadata, but old Linux releases cannot resolve the new stable strings. Windows hardware rebinding remains necessary. Unknown future ID versions are preserved and reported as unsupported, not loosely parsed.

### Golden-file policy

Keep the existing fixture and its expected `hwmon4/curr5` unchanged.

- Existing lossless-round-trip tests explicitly run without a migration service.
- Separate migration tests inject a deterministic catalog containing WireView `curr5`, label `Pin 5`.
- Verify the exact intended ID change, original-ID metadata, and preservation of every other existing XML value.
- Do not weaken the existing general comparison helper to ignore sensor IDs.

## 6. Rescan/hotplug plan

### Schedule and cost

Use one serialized worker with two deadlines:

- Poll at the existing configured interval.
- Perform a metadata rescan every **10 seconds**, including when no sensor is demanded and when both class directories were absent at startup.

Every rescan checks the directory entries, identity metadata, and channel membership. Do not rely exclusively on directory mtime or entry count: replacement can preserve both the number and names of entries.

For the expected number of chips, a metadata scan every 10 seconds is simpler and sufficiently cheap. Identical scans discard their candidate state without rebuilding resolver indexes or the UI.

Leave an optional `RequestRescan()` entry point for a future udev listener. Coalesce requests and debounce bursts; periodic scanning remains the recovery mechanism if events are missed.

### Polling plan

The current monitor repeatedly reads chip names, lists channel files, and reads labels. Replace that work with cached descriptors.

A normal tick performs:

1. Demand snapshot refresh if due or invalidated.
2. Canonical-key demand lookup.
3. One existing input-value read for each demanded channel.
4. Existing numeric parsing, scaling, and min/max updates.
5. Existing `LinuxSystemSensors.Poll()` invocation.

No identity attributes, labels, directory listings, or symlink resolution occur in the normal polling path.

The initial poll may retain the existing one-time full-catalog sampling behavior using a local bypass flag. Subsequent scans do not force-read every existing sensor; new sensors are sampled when demanded or while full polling is active.

### Protect against kernel-path reuse

Caching `/sys/class/hwmon/hwmon2/temp1_input` is insufficient: `hwmon2` may later belong to another device.

During scanning, pin the resolved chip/zone directory with an owned directory handle. Production value readers open channel filenames relative to that handle, using `openat` and safe-handle ownership. When the old kernel object disappears, reads fail rather than following a reused class path.

This keeps approximately one directory handle per chip/zone and does not add value reads. Polling and handle disposal are serialized. Scan-time object identity may be used to detect endpoint replacement, but never enters a persisted ID.

### Publication and failure handling

When a successful scan differs:

1. Build and validate the replacement plan/catalog.
2. Remove readings for departed, ambiguous, or replaced descriptors.
3. Publish the new snapshot and polling plan.
4. Invalidate demand.
5. Notify the UI/store outside the monitor’s publication critical section.
6. Dispose retired polling handles after their last worker use.

On a value read failure, remove that reading immediately so an old number is not presented as current. Keep its descriptor for diagnostics and future demand until a scan establishes removal.

Handle metadata failures per device:

- A disappearing entry does not abort other devices’ discovery.
- A failed directory listing is not evidence that every device vanished.
- A partial scan must not reassign another device’s identity.
- Do not downgrade a known strong binding to unrelated weak hardware because its serial file temporarily became unreadable.

Reappearance with the same durable identity restores the binding automatically and starts a new availability incarnation. Unrelated `system/...` entries must never be removed during hwmon/thermal cleanup.

## 7. Test plan

### Fake sysfs fixture

Inject a root representing `/sys`, rather than only overriding `/sys/class/hwmon`. Identity derivation needs sibling classes and the device tree.

```text
<temp>/sys/
├── class/
│   ├── hwmon/
│   │   ├── hwmon2 -> ../../devices/pci0000:80/0000:81:00.0/nvme/nvme1/hwmon2
│   │   └── hwmon3 -> ../../devices/pci0000:00/0000:01:00.0/nvme/nvme0/hwmon3
│   ├── nvme/
│   │   ├── nvme1 -> ../../devices/pci0000:80/0000:81:00.0/nvme/nvme1
│   │   └── nvme0 -> ../../devices/pci0000:00/0000:01:00.0/nvme/nvme0
│   └── thermal/
│       └── thermal_zone7 -> ../../devices/virtual/thermal/thermal_zone7
└── devices/
    ├── pci0000:80/0000:81:00.0/nvme/nvme1/
    │   ├── serial                  # TEST-NVME-A
    │   ├── model                   # Example model
    │   └── hwmon2/
    │       ├── name                # nvme
    │       ├── device -> ..
    │       ├── temp1_input         # 42000
    │       └── temp1_label         # Composite
    ├── pci0000:00/0000:01:00.0/nvme/nvme0/
    │   ├── serial                  # TEST-NVME-B
    │   └── hwmon3/
    │       ├── name                # nvme
    │       ├── device -> ..
    │       ├── temp1_input         # 31000
    │       └── temp1_label         # Composite
    └── virtual/thermal/thermal_zone7/
        ├── type                    # acpitz
        ├── temp                    # 37000
        └── device -> <fixture ACPI device containing path>
```

Extend the builder with USB, PCI subsystem links, platform, block/SCSI, I²C adapter/mux, and missing-link cases. Use synthetic serials in committed tests.

Use real relative symlinks for Linux filesystem integration tests. A fake `ISysfsAccess` additionally supplies deterministic read counters, exceptions, enumeration permutations, and simulated kernel-object removal. Inject time and a no-op system-provider callback so tests never initialize NVML/ROCm or read the host’s `/proc`.

### Concrete cases

| Area | Required cases and assertions |
|---|---|
| Basic derivation | All seven supported channel prefixes, including `in0`; channel suffix unchanged; existing divisors/units preserved. |
| Enumeration independence | Swap hwmon indices and directory order; IDs and device/value associations remain identical. |
| NVMe numbering | Swap both hwmon and `nvmeN` names while preserving serial/PCI identity; IDs remain identical. |
| Duplicate chip names | Two `nvme` chips with `Composite` labels yield distinct IDs. |
| Duplicate anchors | Same serial at different PCI endpoints yields deterministic qualified IDs. Add/remove either device without renaming the survivor. |
| Irreducible ambiguity | Identical full identity tuples produce diagnostics and no overwriting/bindable key. |
| PCI/platform | NIC MDIO address, AMD/NVIDIA/Intel PCI owners, k10temp, `coretemp.0`, `nct6775.656`, and it87. |
| USB | Serial takes priority; arbitrary ancestor depth; composite-interface role; serial-less stable port path; USB bus/device renumbering. |
| I²C | Logical bus renumbering; same adapter names on different owners; equal client addresses behind different mux channels; generated adapter-name normalization. |
| SATA | Rename block devices and SCSI hosts while preserving WWID/serial; temperature remains attached to the same drive. |
| Thermal | Zone renumbering; same type with distinct ACPI paths; indistinguishable duplicate types; `iwlwifi_1` becoming `iwlwifi_2`. |
| Thermal aggregation | Multiple same-type zones do not obtain guessed `tempN` associations; direct zone bindings remain available. |
| Missing links/attributes | No `device` link but usable ancestry; empty serial; missing name; unreadable metadata; label absent. |
| Encoding | Spaces, XML characters, separators, Unicode, `%`, long tokens, reserved path components, and injected shortening-hash collision. |
| Exact resolution | Stable ID resolves despite a missing reading; a read failure does not select another sensor. |
| Legacy migration | Matching current alias and unique label; stale alias with contradictory label; unique-channel no-hint fallback; thermal legacy IDs. |
| Fuzzy matching | Unique `(chip, channel, label)` match after renumbering; normalization; ambiguous `Composite`; synthetic labels provide no false confidence. |
| Strong-identity guard | Absent serial A never resolves to serial B despite matching chip/channel/label. Unique serial A can recover after a PCI relocation. |
| Cache correctness | Same legacy string with different hints yields different outcomes; negative results invalidate when hardware appears; identical scans preserve generation. |
| Persistence | All five families, graph/bar/donut subclasses, nested groups, original-ID retention, no file write during migration, idempotence, and byte-stable second save. |
| Compatibility | Existing golden files round-trip with migration disabled; Windows and plugin bindings remain unchanged; unknown versions survive round-trip. |
| Import/export | Export includes an unsaved migrated snapshot; unresolved IDs and metadata survive import/export without local hardware. |
| Demand | Legacy item demands its canonical live key; unresolved item does not demand a wrong channel; catalog changes bypass throttle; plugin/hotkey demand remains correct. |
| UI | Duplicate chip names remain separate; add/replace/drag/drop store stable IDs; undo restores the whole binding; rescans preserve selection/search and clear departed values. |
| Graphs | Legacy/stable aliases share history; same canonical migration preserves samples; rebind/removal/replug resets appropriate history and auto-range only. |
| Hotplug | Empty startup followed by appearance; same-count device replacement; channels added late; transient failure; retired handles cannot read replacement data. |
| Concurrency | Parallel resolve/read/demand/UI-catalog reads while publishing snapshots: no collection exceptions, mixed-generation decisions, or stale writer repopulation. |
| Performance | After scan, repeated polls record zero identity/label/listing reads; undemanded inputs record zero value reads; demanded inputs receive one read per tick. |

For the pinned-reader implementation, include a Linux integration test that opens the original directory, unlinks/replaces its pathname, and proves the reader does not follow the replacement. Model sysfs removal errors separately rather than assuming ordinary temporary files behave exactly like kernfs.

Required implementation validation:

```bash
dotnet build InfoPanel.slnx
dotnet test InfoPanel.slnx
```

After automated checks pass, compare `--dump-sensors` output across a real reboot and exercise one available hotplug device. These are implementation acceptance checks, not actions performed for this document.

## 8. Docs to update

### README.md

Add under **Features → Sensors**:

> Hardware sensor bindings use stable identities derived from device serials, PCI or platform locations, and thermal-zone identity. Changes to Linux’s `hwmonN` and `thermal_zoneN` numbering no longer break these bindings. Older profiles are upgraded when InfoPanel can identify one matching sensor; ambiguous or missing bindings keep their original IDs and can be repaired with Replace Sensor in the designer. Identical devices that expose no distinguishing identity may require manual rebinding.

Add under **Architecture**, following demand-driven sensing:

> Hardware discovery builds a shared sensor catalog at startup and refreshes it every 10 seconds. Polling reads only demanded input values from that catalog; device identity and labels are read during discovery. New or reconnected sensors appear automatically, and disconnected sensors stop displaying their previous readings.

Update the command-line description:

> `--dump-sensors` prints canonical sensor IDs, labels, and current availability. Add `--verbose` to include current sysfs paths, legacy aliases, and identity diagnostics.

Add to **Windows interoperability → Sensor bindings**:

> Stable Linux hardware IDs preserve bindings across Linux enumeration changes. Older Linux versions can load these profiles but may require hardware rebinding. Windows hardware bindings still use a different backend; plugin bindings remain portable.

### docs/USER-GUIDE.md

Add under **Sensors**:

> Sensor bindings normally survive reboots even when Linux changes device numbering. InfoPanel checks for hardware changes every 10 seconds, so a newly connected sensor may take that long to appear. Disconnected sensors show no current value and recover automatically when the same identifiable device returns. Devices with the same chip name appear separately using their hardware identity.

Add under **Designer**:

> When an older profile is opened, InfoPanel attempts to match its saved hardware bindings to the current sensors. Successful matches are saved with the next normal profile save. If a binding is missing or ambiguous, its original ID is retained. Select the intended sensor in the Sensors panel and use Replace Sensor to repair the binding; this change supports Undo and Redo.

Add under **Troubleshooting**:

> **Sensor missing after a reboot or hardware change:** allow up to 10 seconds for discovery, then check the Sensors page and the selected item’s binding status in the designer. InfoPanel will not choose arbitrarily between indistinguishable devices. Use Replace Sensor if needed. For diagnostics, run `infopanel --dump-sensors --verbose` and check the logs for the original ID and resolution result. Disk, network, and vendor GPU metrics under `system/...` still have provider-specific naming limitations.

## 9. Risks and open questions

| Topic | Decision and recommendation |
|---|---|
| Legacy profiles have insufficient information | **Decided by this design:** migration is best effort, not historical reconstruction. Require a unique compatible candidate; retain the original ID and report unresolved otherwise. |
| Indistinguishable devices | **Decided by this design:** expose ambiguity rather than inventing a boot-dependent ordinal. No algorithm can recover an identity that the hardware interface does not expose. |
| Duplicate membership changes | **Decided by this design:** use structural secondary discriminators from the first discovery, including singleton cases. Do not assign ordinals based on current group membership. |
| Serial identity versus relocation | **Decided by this design:** include stable topology as a discriminator where available. Allow a unique unchanged strong serial identity to recover after location changes; never cross a conflicting serial. |
| PCI/platform identity strength | **Decided by this design:** document these as location-based identities. Ordinary reboot renumbering is covered; firmware/topology changes and physical replacement can require rebinding. |
| Driver changes | **Decided by this design:** do not automatically equate k10temp and zenpower or renumber channels after driver changes. Preserve the original binding when semantics are uncertain. |
| Thermal type counters and aggregation | **Decided by this design:** normalize known generated counters, preserve raw names for diagnostics, and reject unsupported channel-to-zone guesses. Use direct thermal bindings where aggregation prevents durable channel identity. |
| Metadata temporarily unreadable | **Decided by this design:** do not rewrite a known strong binding to weak or contradictory hardware. Retry during subsequent scans. |
| Labels can change | **Decided by this design:** normal stable identity does not include channel labels. Labels are migration hints and only a last-resort weak discriminator for otherwise unidentified chips. |
| Privacy in exported IDs | **Decided by this design:** retain readable serial-based IDs as requested. Document that exported profiles and verbose dumps can contain device serials; introduce no automatic telemetry. |
| Sysfs topology differences | **Decided by this design:** isolate bus-specific discovery, use subsystem-aware ancestry, test missing links and deeper paths, and avoid external `readlink`, `udevadm`, or SMART commands during scanning. |
| Hotplug detection delay | **Decided by this design:** accept up to 10 seconds initially. Udev acceleration is optional follow-up; periodic scanning is mandatory. |
| Polling cost | **Decided by this design:** metadata reads occur only during scans. Keep existing demand gating before input reads, especially for potentially costly SMBus/drive sensors. |
| Reading/history lifetime | **Decided by this design:** remove failed/departed readings and reset history on availability incarnation changes. Never let a removed endpoint’s last value appear current. |
| `system/disk/<dev>` and `system/block/<dev>` | **Decided by this design:** defer ID changes. Block device names can change. Follow up with disk WWID/serial and namespace identity, reusing this scanner’s identity helpers and migration infrastructure. |
| `system/network/<iface>` | **Decided by this design:** defer ID changes. Predictable interface names are more durable than probe-order names but can change with configuration/topology; virtual names may be transient. Follow up using permanent hardware identity plus stable device context, with explicit handling for virtual interfaces. |
| `system/gpu` / `system/gpu/<i>` | **Decided by this design:** defer provider migration. NVML indices are unstable, and the current single-GPU prefix changes when device count becomes greater than one. Follow up with GPU UUID or PCI identity. |
| `system/amdgpu` / `system/amdgpu/<i>` | **Decided by this design:** defer provider migration. Also fix ROCm’s current sysfs clock-path association, which can select the same first DRM device for multiple indices. Use provider PCI identity to join the two interfaces. |
| `system/igpu/...` | **Decided by this design:** defer provider migration. Its key is unindexed but currently represents whichever Intel card is discovered first. Follow up with explicit device identity. |
| Other system metrics | **Decided by this design:** do not claim all `system/...` IDs are now durable. Audit filesystem, power-supply, CPU-topology, and RAPL naming separately. |
| Persistence scope | **Decided by this design:** no profile-wide schema version or machine-local alias database is needed. Version IDs themselves and store optional identity/provenance alongside each binding. |

## 10. Implementation order

1. **Define Core identity and resolution contracts.**  
   Implement the grammar, encoding, immutable descriptors, references, resolution statuses, and synthetic-catalog resolver tests.  
   **Acceptance:** deterministic round trips; exact/legacy/fuzzy ordering; ambiguity and strong-identity guards; no Linux dependency in Core.

2. **Implement injected sysfs discovery and anchor derivation.**  
   Add `SysfsAccess`, the fake fixture, owner classification, normalization, and deterministic discriminators.  
   **Acceptance:** every required hardware family has fixtures; swapping hwmon, thermal, NVMe, SCSI, and I²C enumeration numbers preserves expected identities; indistinguishable devices are rejected.

3. **Replace hwmon polling with catalog-driven polling and rescanning.**  
   Add the serialized worker, pinned readers, 10-second rescan, stable `SENSORHASH` keys, and stale-reading removal.  
   **Acceptance:** late devices appear; same-count replacements are detected; retired paths cannot read replacement devices; no metadata reads occur during ordinary polling.

4. **Connect read resolution and demand collection.**  
   Update `SensorReader`, sensor models, `SensorDemand`, and host wiring.  
   **Acceptance:** a legacy profile demands and reads the same canonical key; unresolved items never poll an arbitrary candidate; generation changes rebuild demand immediately; plugin/system behavior remains compatible.

5. **Implement persistence migration and complete binding metadata.**  
   Add recursive migration, optional XML metadata, store reconciliation, autosave suppression, and copy/clone support.  
   **Acceptance:** all item families and nested groups migrate in memory without a disk write; unresolved IDs survive; original IDs are retained; ordinary saves persist successful migrations; existing golden tests remain intact.

6. **Update import/export.**  
   Export detached current snapshots and use the normal migration path for imported items.  
   **Acceptance:** an unsaved migration appears in the archive; export does not overwrite the source profile; missing-hardware imports retain bindings and later recover; archive layout is unchanged.

7. **Update sensor browsing and designer binding actions.**  
   Group by chip identity, refresh by catalog generation, expose status, and centralize complete binding application.  
   **Acceptance:** the two NVMe chips appear separately; every creation/replacement path writes stable IDs and hints; undo/redo restores complete bindings; hotplug preserves meaningful selection and filtering.

8. **Update graph history and missing-value rendering.**  
   Extract history ownership, use canonical ID plus incarnation, and remove zero/first-frame substitutes for unresolved hardware bindings.  
   **Acceptance:** aliases share history; migration to the same key preserves it; rebind/removal/replug cannot mix samples; missing sensors visibly show no current value.

9. **Complete concurrency, performance, and compatibility validation.**  
   Add stress tests, read counters, serialized static-seam fixtures, and solution registration.  
   **Acceptance:** `dotnet build InfoPanel.slnx` and `dotnet test InfoPanel.slnx` pass; polling performs no identity reads; catalog publication does not produce collection races or stale writer updates.

10. **Update documentation and validate on real hardware.**  
    Add the supplied paragraphs and compare canonical dumps before/after reboot; exercise hotplug where available.  
    **Acceptance:** this machine’s two NVMe bindings remain attached to their respective serials; WireView/coretemp/thermal bindings retain identity; unresolved cases are diagnosable; deferred `system/...` limitations are documented.