// ============================================================================
// mods/SpawnAnimal/mod.cs  (server-side, ASCII)
// Spawn by command / by coords / from config.ini
// No corpse-ticks – only EVENT SimGroup::onObjectAdded
// Supports "I = count" per rule in config.ini + region limit (50 + r).
// Auto-spawn from config.ini starts ONLY after first player enters the game.
// ============================================================================

// --- Hot reload package
if (isFunction("deactivatePackage") && isPackage("SpawnAnimalPkg"))
{
   deactivatePackage(SpawnAnimalPkg);
}

// ============================================================================
// Global config
// ============================================================================

$SA_LIFT                 = 2.0;       // how high above terrain to lift spawn
$SA_PROBE_R              = 4.0;       // terrain probe radius
$SA_PROBE_STEP           = 1.0;

$SA_CORPSE_EVENT_RADIUS  = 0.5;       // max distance between corpse and our Animal to link them
$SA_CORPSE_KILL_DELAY_MS = 600;       // delay before deleting Animal after corpse detected (ms)

$SA_CFG_FILE             = "mods/SpawnAnimal/config.ini";

// flag: have we already started auto-spawns from config?
$SA_ConfigStarted        = 0;

// ============================================================================
// General helpers
// ============================================================================

function SA_min(%a, %b) { return (%a < %b ? %a : %b); }
function SA_max(%a, %b) { return (%a > %b ? %a : %b); }

function SA_name(%o)
{
   if (!isObject(%o))
      return "0";
   %db  = (%o.isMethod("getDataBlock") ? %o.getDataBlock() : 0);
   %dbn = (isObject(%db) ? %db.getName() : "(noDB)");
   return %o.getId() @ ":" @ %dbn;
}

function SA_isOurAnimal(%o)
{
   return (isObject(%o) &&
           %o.getClassName() $= "Animal" &&
           %o.spawnedByMod);
}

function SA_toInt(%v)
{
   return mFloor(%v + 0);
}

function SA_toFloat(%v)
{
   return %v + 0.0;
}

// ============================================================================
// Terrain / Z / water
// ============================================================================

function SA_getTerrainAABB(%out)
{
   %has  = false;
   %minX = 1e9;
   %minY = 1e9;
   %maxX = -1e9;
   %maxY = -1e9;

   if (isObject(MissionGroup))
   {
      for (%i = 0; %i < MissionGroup.getCount(); %i++)
      {
         %o = MissionGroup.getObject(%i);
         if (isObject(%o) && %o.getClassName() $= "TerrainBlock")
         {
            %wb   = %o.getWorldBox();
            %minX = SA_min(%minX, getWord(%wb, 0));
            %minY = SA_min(%minY, getWord(%wb, 1));
            %maxX = SA_max(%maxX, getWord(%wb, 3));
            %maxY = SA_max(%maxY, getWord(%wb, 4));
            %has  = true;
         }
      }
   }

   if (%has)
   {
      %out.minX = %minX;
      %out.minY = %minY;
      %out.maxX = %maxX;
      %out.maxY = %maxY;
   }

   return %has;
}

function SA_clampXY(%x, %y)
{
   %box = new ScriptObject();
   %ok  = SA_getTerrainAABB(%box);

   if (!%ok)
   {
      %box.delete();
      return %x SPC %y;
   }

   %cx = (%x < %box.minX + 1 ? %box.minX + 1 :
          (%x > %box.maxX - 1 ? %box.maxX - 1 : %x));
   %cy = (%y < %box.minY + 1 ? %box.minY + 1 :
          (%y > %box.maxY - 1 ? %box.maxY - 1 : %y));

   %box.delete();
   return %cx SPC %cy;
}

function SA_raycastZ(%x, %y)
{
   %start = %x SPC %y SPC 5000;
   %end   = %x SPC %y SPC -5000;
   %mask  = $TypeMasks::TerrainObjectType
          | $TypeMasks::StaticObjectType
          | $TypeMasks::WaterObjectType
          | $TypeMasks::InteriorObjectType;
   %hit   = containerRayCast(%start, %end, %mask, 0);
   if (%hit $= "")
      return "";
   return getWord(%hit, 3);
}

function SA_isWaterAt(%x, %y, %z)
{
   %p1  = %x SPC %y SPC (%z + 1);
   %p2  = %x SPC %y SPC (%z - 1);
   %hit = containerRayCast(%p1, %p2, $TypeMasks::WaterObjectType, 0);
   return (%hit !$= "");
}

function SA_findSurfaceZ(%x, %y, %fallbackZ)
{
   %xy = SA_clampXY(%x, %y);
   %x  = getWord(%xy, 0);
   %y  = getWord(%xy, 1);

   %bestHit = false;
   %bestZ   = -1e9;

   %z0 = SA_raycastZ(%x, %y);
   if (%z0 !$= "")
   {
      %bestZ   = %z0;
      %bestHit = true;
   }

   %r = ($SA_PROBE_R $= "" ? 4.0 : $SA_PROBE_R);
   %s = ($SA_PROBE_STEP $= "" ? 1.0 : $SA_PROBE_STEP);

   for (%ox = -%r; %ox <= %r; %ox += %s)
   {
      for (%oy = -%r; %oy <= %r; %oy += %s)
      {
         if (%ox == 0 && %oy == 0)
            continue;

         %z1 = SA_raycastZ(%x + %ox, %y + %oy);
         if (%z1 $= "")
            continue;

         if (%z1 > %bestZ)
         {
            %bestZ   = %z1;
            %bestHit = true;
         }
      }
   }

   if (%bestHit)
      return %bestZ;

   if (%fallbackZ $= "")
      %fallbackZ = 1.0;

   return %fallbackZ;
}

function SA_offset(%x, %y, %radius)
{
   if (%radius $= "" || %radius <= 0)
      %radius = 0.8;

   %ang = getRandom() * 6.2831853;
   %nx  = %x + mCos(%ang) * %radius;
   %ny  = %y + mSin(%ang) * %radius;

   return %nx SPC %ny;
}

// ============================================================================
// Clients
// ============================================================================

function SA_clientCount()
{
   return isObject(ClientGroup) ? ClientGroup.getCount() : 0;
}

function SA_findConnByCharId(%charId)
{
   %n = SA_clientCount();

   for (%i = 0; %i < %n; %i++)
   {
      %cl = ClientGroup.getObject(%i);
      if (isObject(%cl) && %cl.charId == %charId)
         return %cl;
   }

   if (%n == 1)
      return ClientGroup.getObject(0);

   return 0;
}

function listOnlineClients()
{
   %n = SA_clientCount();
   echo("[SA] online=" @ %n);

   for (%i = 0; %i < %n; %i++)
   {
      %cl = ClientGroup.getObject(%i);
      echo("[" @ %i @ "] conn=" @ %cl.getId()
           @ " charId=" @ %cl.charId
           @ " hasPlayer=" @ (isObject(%cl.player) ? 1 : 0));
   }
}

// ============================================================================
// Our animals registry
// ============================================================================

if (!isObject(SpawnAnimalSet))
   new SimSet(SpawnAnimalSet);

function SA_ensureSet()
{
   if (!isObject(SpawnAnimalSet))
      new SimSet(SpawnAnimalSet);
   return SpawnAnimalSet;
}

// ============================================================================
// Low-level spawn
// ============================================================================

function SA_spawnOne(%scopeClient, %breed, %q, %x, %y, %z, %yawDeg)
{
   if (%breed $= "")
   {
      error("[SA] breed empty");
      return 0;
   }

   if (%q $= "")
      %q = 60;
   %q = SA_toInt(%q);
   if (%yawDeg $= "")
      %yawDeg = 0;

   %xy2 = SA_offset(%x, %y, 0.8);
   %x2  = getWord(%xy2, 0);
   %y2  = getWord(%xy2, 1);

   %zSurf = SA_findSurfaceZ(%x2, %y2, %z);

   if (SA_isWaterAt(%x2, %y2, %zSurf))
   {
      for (%i = 0; %i < 6; %i++)
      {
         %xy2   = SA_offset(%x, %y, 1 + %i * 0.7);
         %x2    = getWord(%xy2, 0);
         %y2    = getWord(%xy2, 1);
         %zSurf = SA_findSurfaceZ(%x2, %y2, %z);
         if (!SA_isWaterAt(%x2, %y2, %zSurf))
            break;
      }
   }

   %gz  = %zSurf + ($SA_LIFT $= "" ? 0.85 : $SA_LIFT);
   %rot = "0 0 1 " @ mDegToRad(%yawDeg);

   %an = new Animal()
   {
      dataBlock = %breed;
      position  = %x2 SPC %y2 SPC %gz;
      rotation  = %rot;
   };

   if (!isObject(%an))
   {
      error("[SA] new Animal() failed");
      return 0;
   }

   MissionCleanup.add(%an);
   SA_ensureSet().add(%an);

   %an.spawnedByMod = true;

   if (%an.isMethod("setTemporary"))
      %an.setTemporary(true);
   if (%an.isMethod("setPersistent"))
      %an.setPersistent(false);
   if (%an.isMethod("setSaveToDB"))
      %an.setSaveToDB(false);
   if (%an.isMethod("setQuality"))
      %an.setQuality(%q);
   if (%an.isMethod("setActive"))
      %an.setActive(true);

   if (isObject(%scopeClient))
      %an.scopeToClient(%scopeClient);
   else
      %an.setScopeAlways();

   echo("[SA] spawn id=" @ %an.getId()
        @ " pos=" @ %x2 SPC %y2 SPC %gz
        @ " breed=" @ %breed
        @ " q=" @ %q);

   return %an;
}

// ============================================================================
// Spawn – command API
// ============================================================================

// spawnAtChar(charId, BearData, 60, 0);
function spawnAtChar(%charId, %breed, %q, %yawDeg)
{
   if (%charId $= "")
   {
      %cl = (SA_clientCount() == 1 ? ClientGroup.getObject(0) : 0);
   }
   else
   {
      %cl = SA_findConnByCharId(%charId);
   }

   if (!isObject(%cl) || !isObject(%cl.player))
   {
      error("[SA] spawnAtChar: no active player for charId=" @ %charId);
      return 0;
   }

   %p = %cl.player.getPosition();
   %x = getWord(%p, 0);
   %y = getWord(%p, 1);
   %z = getWord(%p, 2);

   %an = SA_spawnOne(%cl, %breed, %q, %x, %y, %z, %yawDeg);
   return %an;
}

// spawnAt(BearData, 60, x, y, z, yawDeg, charIdForScope);
function spawnAt(%breed, %q, %x, %y, %z, %yawDeg, %charIdForScope)
{
   if (%charIdForScope $= "")
      %cl = (SA_clientCount() == 1 ? ClientGroup.getObject(0) : 0);
   else
      %cl = SA_findConnByCharId(%charIdForScope);

   %an = SA_spawnOne(%cl, %breed, %q, %x, %y, %z, %yawDeg);
   return %an;
}

// spawnCluster(BearData, 5, cx, cy, cz, 10, 60, charIdForScope);
function spawnCluster(%breed, %count, %cx, %cy, %cz, %radius, %q, %charIdForScope)
{
   if (%count $= "" || %count < 1)
      %count = 5;
   if (%radius $= "" || %radius <= 0)
      %radius = 10;

   if (%charIdForScope $= "")
      %cl = (SA_clientCount() == 1 ? ClientGroup.getObject(0) : 0);
   else
      %cl = SA_findConnByCharId(%charIdForScope);

   %ids = "";
   for (%i = 0; %i < %count; %i++)
   {
      %xy = SA_offset(%cx, %cy, %radius * (0.4 + 0.6 * getRandom()));
      %x  = getWord(%xy, 0);
      %y  = getWord(%xy, 1);

      %an = SA_spawnOne(%cl, %breed, (%q $= "" ? 60 : %q),
                        %x, %y, %cz, mFloor(getRandom() * 360));
      if (isObject(%an))
         %ids = %ids @ (%ids $= "" ? "" : " ") @ %an.getId();
   }

   echo("[SA] cluster " @ %breed @ " -> " @ %ids);
   return %ids;
}

// ============================================================================
// Tools: list / manual delete
// ============================================================================

//listSpawned();
function listSpawned()
{
   SA_ensureSet();
   %n = SpawnAnimalSet.getCount();
   echo("[SA] list total=" @ %n);
   for (%i = 0; %i < %n; %i++)
   {
      %o = SpawnAnimalSet.getObject(%i);
      echo("[" @ %i @ "] id=" @ %o.getId()
           @ " class=" @ %o.getClassName()
           @ " pos=" @ %o.getPosition());
   }
}

//deleteById(%id);
function deleteById(%id)
{
   %o = (%id !$= "" && isObject(%id)) ? %id : 0;
   if (!isObject(%o))
   {
      error("[SA] deleteById: no such id " @ %id);
      return 0;
   }

   if (isObject(SpawnAnimalSet) && SpawnAnimalSet.isMember(%o))
      SpawnAnimalSet.remove(%o);

   %o.delete();
   echo("[SA] deleted id=" @ %id);
   return 1;
}

// deleteAll();
function deleteAll()
{
   if (!isObject(SpawnAnimalSet))
   {
      %k = 0;
      if (isObject(MissionCleanup))
      {
         for (%i = MissionCleanup.getCount() - 1; %i >= 0; %i--)
         {
            %o = MissionCleanup.getObject(%i);
            if (isObject(%o) &&
                %o.getClassName() $= "Animal" &&
                %o.spawnedByMod)
            {
               %o.delete();
               %k++;
            }
         }
      }
      echo("[SA] deleteAll fallback=" @ %k);
      return %k;
   }

   %n = SpawnAnimalSet.getCount();
   for (%i = %n - 1; %i >= 0; %i--)
   {
      %o = SpawnAnimalSet.getObject(%i);
      SpawnAnimalSet.remove(%o);
      if (isObject(%o))
         %o.delete();
   }

   echo("[SA] deleteAll OK");
   return %n;
}

// ============================================================================
// CORPSE EVENT: handle corpse
// ============================================================================

function SA_findClosestOurAnimal(%pos, %maxDist)
{
   if (!isObject(SpawnAnimalSet))
      return 0;

   %best    = 0;
   %bestDst = 1e9;

   %n = SpawnAnimalSet.getCount();
   for (%i = 0; %i < %n; %i++)
   {
      %an = SpawnAnimalSet.getObject(%i);
      if (!isObject(%an))
         continue;

      if (!SA_isOurAnimal(%an))
         continue;

      %apos = %an.getPosition();
      %dst  = vectorDist(%pos, %apos);

      if (%dst < %maxDist && %dst < %bestDst)
      {
         %best    = %an;
         %bestDst = %dst;
      }
   }

   if (isObject(%best))
      return %best;
   return 0;
}

function SA_killAnimalDelayed(%anId)
{
   %an = %anId;
   if (!isObject(%an))
      return;

   echo("[SA:CORPSE:KILL] removing Animal " @ SA_name(%an));

   if (isObject(SpawnAnimalSet) && SpawnAnimalSet.isMember(%an))
      SpawnAnimalSet.remove(%an);

   // detach from spawn rule, if any
   if (%an.sa_spawnRuleId !$= "")
   {
      %rule = %an.sa_spawnRuleId;
      if (isObject(%rule) && isObject(%rule.animalSet))
      {
         if (%rule.animalSet.isMember(%an))
            %rule.animalSet.remove(%an);
      }
   }

   %an.delete();
}

function SA_handleCorpseObject(%group, %obj)
{
   if (!isObject(%obj))
      return;

   %cls = %obj.getClassName();
   if (%cls !$= "TSStatic" && %cls !$= "ComplexObject")
      return;

   %pos = (%obj.isMethod("getPosition") ? %obj.getPosition() : "");
   if (%pos $= "")
      return;

   %rad = ($SA_CORPSE_EVENT_RADIUS $= "" ? 0.5 : $SA_CORPSE_EVENT_RADIUS);

   %an = SA_findClosestOurAnimal(%pos, %rad);
   if (!isObject(%an))
      return;

   %dst = mFloatLength(vectorDist(%pos, %an.getPosition()), 4);

   echo("[SA:CORPSE:TRIGGER] corpseObj=" @ %obj.getId()
        @ " class=" @ %cls
        @ " pos=" @ %pos
        @ " -> matchAnimal=" @ SA_name(%an)
        @ " dist=" @ %dst);

   %delay = ($SA_CORPSE_KILL_DELAY_MS $= "" ? 1000 : $SA_CORPSE_KILL_DELAY_MS);
   schedule(%delay, 0, "SA_killAnimalDelayed", %an);
}

// ============================================================================
// CONFIG.INI – auto spawn from file (with I = count)
// ============================================================================

function SA_getPosFromGeo(%geoId)
{
   %geoId = trim(%geoId);
   if (%geoId $= "")
      return "";

   // we use BearData only as a helper to resolve GeoID -> world pos
   if (!isObject(BearData))
   {
      echo("[SA] SA_getPosFromGeo: BearData not found, geo=" @ %geoId);
      return "";
   }

   %tmp = new Animal()
   {
      dataBlock = BearData;
      position  = "0 0 0";
   };

   if (!isObject(%tmp))
      return "";

   MissionCleanup.add(%tmp);

   %ok = %tmp.TeleportTo(%geoId);
   if (!%ok)
   {
      echo("[SA] TeleportTo(" @ %geoId @ ") failed");
      %pos = "";
   }
   else
   {
      %pos = %tmp.getPosition();
   }

   %tmp.delete();
   return %pos;
}

function SA_ensureRuleSet()
{
   if (!isObject(SA_RuleSet))
      new SimSet(SA_RuleSet);
   return SA_RuleSet;
}

// Rule object fields:
//   maxCount, dbName, quality, geoId, hasPos, pos, radius, periodMin,
//   animalSet (SimSet of alive animals), scheduleHandle
function SA_makeRule(%count, %dbName, %q, %geoId, %hasPos, %pos, %radius, %Tmin)
{
   %count = SA_toInt(%count);
   %Tmin  = SA_toFloat(%Tmin);

   if (%count < 1)
      %count = 1;

   if (%dbName $= "" || %q $= "")
   {
      echo("[SA:CFG] SA_makeRule: empty dbName or quality");
      return 0;
   }

   if (%geoId $= "" && !%hasPos)
   {
      echo("[SA:CFG] SA_makeRule: neither GeoID nor pos specified");
      return 0;
   }

   if (%Tmin <= 0)
   {
      echo("[SA:CFG] SA_makeRule: T<=0 for db=" @ %dbName);
      return 0;
   }

   %r = new ScriptObject()
   {
      class      = "SA_SpawnRule";
      maxCount   = %count;
      dbName     = %dbName;
      quality    = %q;
      geoId      = %geoId;
      hasPos     = %hasPos;
      pos        = %pos;
      radius     = (%radius $= "" ? 0 : %radius);
      periodMin  = %Tmin;
      scheduleHandle = 0;
   };

   %r.animalSet = new SimSet();
   SA_ensureRuleSet().add(%r);

   echo("[SA:CFG] rule created count=" @ %count
        @ " db=" @ %dbName
        @ " q=" @ %q
        @ " geoId=" @ %geoId
        @ " pos=" @ %pos
        @ " r=" @ %r.radius
        @ " T=" @ %Tmin);

   return %r;
}

// resolve pos for rule (from GeoID if needed)
function SA_ruleResolvePos(%r)
{
   if (!isObject(%r))
      return "";

   if (%r.hasPos && %r.pos !$= "")
      return %r.pos;

   if (%r.geoId !$= "")
   {
      %pos = SA_getPosFromGeo(%r.geoId);
      if (%pos $= "")
         return "";

      %r.pos    = %pos;
      %r.hasPos = true;
      return %pos;
   }

   return "";
}

// count & cleanup alive animals in this rule (without region cull)
function SA_SpawnRule::getAliveCount(%this)
{
   %alive = 0;

   if (!isObject(%this.animalSet))
      return 0;

   for (%i = %this.animalSet.getCount() - 1; %i >= 0; %i--)
   {
      %an = %this.animalSet.getObject(%i);
      if (!isObject(%an) || %an.getClassName() !$= "Animal")
      {
         %this.animalSet.remove(%an);
         continue;
      }

      %alive++;
   }

   return %alive;
}

// actual spawn + region cull + re-schedule
function SA_SpawnRule::doSpawn(%this)
{
   // resolve center position (GeoID or pos)
   %pos = SA_ruleResolvePos(%this);
   if (%pos $= "")
   {
      echo("[SA:RULE] cannot resolve pos for rule db=" @ %this.dbName
           @ " geoId=" @ %this.geoId);
      %nextMs = (%this.periodMin * 60 * 1000);
      %this.scheduleHandle = %this.schedule(%nextMs, "doSpawn");
      return;
   }

   %cx = getWord(%pos, 0);
   %cy = getWord(%pos, 1);
   %cz = getWord(%pos, 2);

   // compute spawn radius (for distribution)
   %baseRadius = %this.radius;
   if (%baseRadius <= 0)
   {
      if (%this.maxCount > 1)
         %baseRadius = 10 * mSqrt(%this.maxCount);   // auto spread for herds
      else
         %baseRadius = 0;
   }

   // compute region limit: 50 + baseRadius
   %limitR = 50 + %baseRadius;

   // region cull – remove animals that left region (anti-exploit, anti-bug)
   if (isObject(%this.animalSet))
   {
      for (%i = %this.animalSet.getCount() - 1; %i >= 0; %i--)
      {
         %an = %this.animalSet.getObject(%i);
         if (!isObject(%an) || %an.getClassName() !$= "Animal")
         {
           %this.animalSet.remove(%an);
           continue;
         }

         %apos = %an.getPosition();
         %dst  = vectorDist(%pos, %apos);

         if (%dst > %limitR)
         {
            echo("[SA:RULE:OUT] removing Animal out of region db=" @ %this.dbName
                 @ " an=" @ SA_name(%an)
                 @ " dist=" @ mFloatLength(%dst, 2)
                 @ " limit=" @ %limitR);

            %this.animalSet.remove(%an);
            if (isObject(SpawnAnimalSet) && SpawnAnimalSet.isMember(%an))
               SpawnAnimalSet.remove(%an);
            %an.delete();
         }
      }
   }

   // now count alive after region cleanup
   %alive = %this.getAliveCount();

   // HARD cap: if already >= maxCount, absolutely no new spawns
   if (%alive >= %this.maxCount)
   {
      %nextMs = (%this.periodMin * 60 * 1000);
      %this.scheduleHandle = %this.schedule(%nextMs, "doSpawn");
      return;
   }

   %need  = %this.maxCount - %alive;

   %db = %this.dbName;
   if (!isObject(%db))
   {
      echo("[SA:RULE] invalid datablock " @ %this.dbName);
   }
   else
   {
      for (%k = 0; %k < %need; %k++)
      {
         if (%baseRadius > 0)
         {
            %xy = SA_offset(%cx, %cy, %baseRadius * (0.4 + 0.6 * getRandom()));
            %sx = getWord(%xy, 0);
            %sy = getWord(%xy, 1);
         }
         else
         {
            %sx = %cx;
            %sy = %cy;
         }

         %sz = %cz;

         %an = SA_spawnOne(0, %db, %this.quality, %sx, %sy, %sz,
                           mFloor(getRandom() * 360));
         if (isObject(%an))
         {
            %an.sa_spawnRuleId = %this.getId();
            if (!isObject(%this.animalSet))
               %this.animalSet = new SimSet();
            %this.animalSet.add(%an);
         }
      }
   }

   // Safety clamp: if somehow we have more than maxCount, remove extras.
   %alive2 = %this.getAliveCount();
   if (%alive2 > %this.maxCount && isObject(%this.animalSet))
   {
      %over = %alive2 - %this.maxCount;
      for (%i = %this.animalSet.getCount() - 1; %i >= 0 && %over > 0; %i--)
      {
         %an2 = %this.animalSet.getObject(%i);
         if (!isObject(%an2))
         {
            %this.animalSet.remove(%an2);
            continue;
         }

         echo("[SA:RULE:CLAMP] too many animals for rule db=" @ %this.dbName
              @ " removing extra " @ SA_name(%an2));

         %this.animalSet.remove(%an2);
         if (isObject(SpawnAnimalSet) && SpawnAnimalSet.isMember(%an2))
            SpawnAnimalSet.remove(%an2);
         %an2.delete();
         %over--;
      }
   }

   %nextMs = (%this.periodMin * 60 * 1000);
   %this.scheduleHandle = %this.schedule(%nextMs, "doSpawn");
}

// read config.ini
function SA_loadConfig()
{
   if (isObject(SA_RuleSet))
      SA_RuleSet.delete();
   new SimSet(SA_RuleSet);

   %file = $SA_CFG_FILE;
   if (!isFile(%file))
   {
      echo("[SA:CFG] no config.ini: " @ %file);
      return;
   }

   %fo = new FileObject();
   if (!%fo.openForRead(%file))
   {
      echo("[SA:CFG] cannot open " @ %file);
      %fo.delete();
      return;
   }

   %lineNo = 0;
   while (!%fo.isEOF())
   {
      %line   = %fo.readLine();
      %lineNo++;

      %trim = trim(%line);
      if (%trim $= "")
         continue;

      %c  = getSubStr(%trim, 0, 1);
      %c2 = getSubStr(%trim, 0, 2);
      if (%c $= "#" || %c $= ";" || %c2 $= "//")
         continue;

      // strip optional parentheses
      if (getSubStr(%trim, 0, 1) $= "(" &&
          getSubStr(%trim, strLen(%trim) - 1, 1) $= ")")
      {
         %trim = getSubStr(%trim, 1, strLen(%trim) - 2);
         %trim = trim(%trim);
      }

      %l  = strreplace(%trim, ",", " ");
      %l  = trim(%l);
      %wc = getWordCount(%l);
      if (%wc < 4)
      {
         echo("[SA:CFG] line " @ %lineNo @ ": too short -> " @ %trim);
         continue;
      }

      // format:
      //   0: count (I)
      //   1: dbName
      //   2: quality
      //   3..: pos / GeoID / r / T

      %count  = getWord(%l, 0);
      %dbName = getWord(%l, 1);
      %q      = getWord(%l, 2);

      %geoId    = "";
      %radius   = 0;
      %Tmin     = 0;
      %hasPos   = false;
      %pos      = "";

      for (%i = 3; %i < %wc; %i++)
      {
         %w  = getWord(%l, %i);
         %wl = strlwr(%w);

         if (getSubStr(%wl, 0, 4) $= "pos=")
         {
            %vx = getSubStr(%w, 4, 1024);
            %vy = (%i + 1 < %wc ? getWord(%l, %i + 1) : "");
            %vz = (%i + 2 < %wc ? getWord(%l, %i + 2) : "");
            %pos    = %vx SPC %vy SPC %vz;
            %hasPos = true;
            %i += 2;
            continue;
         }

         if (getSubStr(%wl, 0, 6) $= "geoid=")
         {
            %geoId = getSubStr(%w, 6, 1024);
            continue;
         }

         if (getSubStr(%wl, 0, 2) $= "r=")
         {
            %radius = getSubStr(%w, 2, 1024);
            continue;
         }

         if (getSubStr(%wl, 0, 2) $= "t=")
         {
            %Tmin = getSubStr(%w, 2, 1024);
            continue;
         }
      }

      %rule = SA_makeRule(%count, %dbName, %q, %geoId, %hasPos, %pos, %radius, %Tmin);
      if (!isObject(%rule))
      {
         echo("[SA:CFG] line " @ %lineNo @ ": invalid rule -> " @ %trim);
      }
   }

   %fo.close();
   %fo.delete();

   echo("[SA:CFG] loaded rules: " @ SA_RuleSet.getCount());
}

function SA_startAutoSpawns()
{
   if (!isObject(SA_RuleSet))
      return;

   %n = SA_RuleSet.getCount();
   for (%i = 0; %i < %n; %i++)
   {
      %r = SA_RuleSet.getObject(%i);
      if (!isObject(%r))
         continue;

      if (%r.periodMin <= 0)
         continue;

      %r.doSpawn();
   }
}

function SA_reloadConfig()
{
   SA_loadConfig();
   SA_startAutoSpawns();
   $SA_ConfigStarted = 1;
}

// (first player enters game)
function SA_ensureStarted()
{
   if ($SA_ConfigStarted)
      return;

   SA_reloadConfig();
}

// ============================================================================
// PACKAGE – corpse onObjectAdded + delayed config start
// ============================================================================

package SpawnAnimalPkg
{
   function SimGroup::onObjectAdded(%this, %obj)
   {
      SA_handleCorpseObject(%this, %obj);
   }

   function RootGroup::onObjectAdded(%this, %obj)
   {
      SA_handleCorpseObject(%this, %obj);
   }

   // first player entering game will kick off config-based spawns
   function GameConnection::onClientEnterGame(%this)
   {
      Parent::onClientEnterGame(%this);
      SA_ensureStarted();
   }
};

activatePackage(SpawnAnimalPkg);
echo("[SA] SpawnAnimalPkg activated");
