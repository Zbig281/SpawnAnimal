# LiF Custom Animal Spawner

This mod lets you define fully automatic, server-side animal spawns from a simple config.ini file.
Animals are spawned by rules (position), cleaned up when they die (corpse trigger from the engine),
and never exceed the amount you specify.

Everything runs on the server — no client files are required.

---------------------------------------------------------------------

## What the mod does

1. Spawns animals from rules defined in config.ini (by pos, optionally within a radius).
2. Listens to the engine event that creates corpse objects (TSStatic / ComplexObject) and matches them to your spawned animals.
3. When a corpse appears on top of one of your animals, that animal is removed shortly after (prevents infinite corpse loops).

The mod also enforces the maximum number of animals per rule and removes animals that are dragged too far from their habitat.

---------------------------------------------------------------------

## Server installation

Place the mod on the server like this:

Life is Feudal Your Own Dedicated Server/
  mods/
    SpawnAnimal/
      mod.cs
      config.ini

LiFx autoloads the mod at server startup.

You can still reload manually from console:

    exec("mods/SpawnAnimal/mod.cs");

No client setup required.

---------------------------------------------------------------------

## Configuration: config.ini

Each active rule is one line in this exact format (NO SPACES around commas):

    (I,DataBlockName,Quality,pos=X Y Z,T=MINUTES)
    (I,DataBlockName,Quality,pos=X Y Z,r=R,T=MINUTES)

Where:

I                 = how many animals the rule wants alive at the same time  
DataBlockName     = animal datablock (Example: BearData, WolfData, TribeBData...)  
Quality           = value for setQuality()  
pos=X Y Z         = fixed world position  
r=R               = radius around the center (optional)  
T=MINUTES         = respawn frequency  

Important rules:

- NO spaces around commas:
      (3,WolfData,60,pos=973.473 -19.7167 1013.5,r=30,T=2)   ← OK
      (3, WolfData, 60, pos=973.473 -19.7167 1013.5, r=30, T=2) ← INVALID

- Spaces inside pos= ARE allowed:
      pos=973.473 -19.7167 1013.5

---------------------------------------------------------------------

## Example config.ini

    # 1 wolf at an exact position, check every 1 minute
    (1,WolfData,50,pos=973.473 -19.7167 1013.5,T=1)

    # 5 bears around a camp location, radius 40, respawn every 5 minutes
    (5,BearData,80,pos=327.964 1416.77 1536.3,r=40,T=5)

Add as many rules as you wish — each works independently.

---------------------------------------------------------------------

## How spawning works

For each rule defined in config.ini, the mod creates a scheduler when the FIRST PLAYER joins the server.

Each rule stores:

- target count (I)
- datablock name
- base position (pos)
- radius r
- timer interval T
- list of currently alive animals spawned by this rule

Every T minutes:

1. Count alive animals for this rule.
2. If alive < I, spawn missing animals.
3. If animals are too far away (distance > 50 + r), delete them.
4. Maintain exactly I animals alive.

Spawn logic:

- resolves height from terrain and avoids water
- applies rotation, random offset when radius is used
- marks animals as spawnedByMod

---------------------------------------------------------------------

## Death detection and corpse trigger

Instead of polling damage or HP, the mod hooks engine events:

SimGroup::onObjectAdded
RootGroup::onObjectAdded

When the engine creates a corpse object (TSStatic or ComplexObject):

1. The mod does a radius search to find the closest spawned animal.
2. If the corpse is exactly on that animal's position, the mod schedules a delayed kill (about 1 second).
3. After delay, the animal is removed and deregistered from its rule.

This prevents infinite corpse creation loops that vanilla engine causes for modded animals.

---------------------------------------------------------------------

## Manual admin commands

From console you can use:

    spawnAtChar(charId, datablock, quality, yaw)
    spawnAt(datablock, quality, x, y, z, yaw, charId)
    spawnCluster(datablock, count, cx, cy, cz, radius, quality, charId)
    listSpawned()
    deleteById(objectId)
    deleteAll()
    SA_reloadConfig()

All these animals behave like animals from rules (corpse cleanup etc.).

---------------------------------------------------------------------

## Requirements

- LiF YO Dedicated Server
- LiFx Framework (autoload + hooks)
- No client files required

## Modding & Editing
You’re free to modify, edit, expand, and use this mod however you like.
Everything is open for the community, feel free to build on top of it!

## Credits
Original base created by Zbig Brodaty.
If you use anything from this mod, please mention that it was created by Zbig Brodaty.
