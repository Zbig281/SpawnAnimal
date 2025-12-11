// --- Hot reload pakietu
if (isFunction("deactivatePackage") && isPackage("SpawnAnimalPkg"))
{
   deactivatePackage(SpawnAnimalPkg);
}

// ============================================================================
// Konfiguracja globalna
// ============================================================================

$SA_LIFT          = 2.0;    // if we lift the spawn above the surface
$SA_PROBE_R       = 4.0;    // additional search radius Z
$SA_PROBE_STEP    = 1.0;
$SA_KILL_DELAY_MS = 600;   // delay in killing the bear after detecting the carcass (ms)

$SA_CFG_PATH      = "mods/SpawnAnimal/config.ini";

// ============================================================================
// Helpery
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
   return (isObject(%o) && %o.getClassName() $= "Animal" && %o.sa_spawnedByMod);
}

function SA_toInt(%v)
{
   %v = trim(%v);
   if (%v $= "")
      return 0;
   return mFloor(%v + 0);
}

function SA_toFloat(%v)
{
   %v = trim(%v);
   if (%v $= "")
      return 0.0;
   return %v + 0.0;
}

// ============================================================================
// Terrain / Z / woda
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
// Klienci + komenda pos(CharID)
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

// pos(CharID) – szybki podgląd pozycji gracza do config.ini
function pos(%charId)
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
      echo("[SA:POS] no active player for charId=" @ %charId);
      return "";
   }

   %p = %cl.player.getPosition();
   echo("[SA:POS] charId=" @ %cl.charId @ " pos=" @ %p);
   return %p;
}

// ============================================================================
// Rejestr naszych zwierząt
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
// Spawn – KOŁO GRACZA + inne
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

   %an.sa_spawnedByMod = true;
   %an.sa_corpseDebug  = true;

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

// Spawn KOŁO GRACZA
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

// Spawn na konkretnych koordach
function spawnAt(%breed, %q, %x, %y, %z, %yawDeg, %charIdForScope)
{
   if (%charIdForScope $= "")
      %cl = (SA_clientCount() == 1 ? ClientGroup.getObject(0) : 0);
   else
      %cl = SA_findConnByCharId(%charIdForScope);

   %an = SA_spawnOne(%cl, %breed, %q, %x, %y, %z, %yawDeg);
   return %an;
}

// Spawn grupowy (ręczny)
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
// Narzędzia: lista / usuwanie
// ============================================================================

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
                %o.sa_spawnedByMod)
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
// Corpse trigger (EVENT) – używa onObjectAdded, bez ticka
// ============================================================================

function SA_findClosestOurAnimal(%pos, %maxDist)
{
   if (!isObject(SpawnAnimalSet))
      return 0;

   %best   = 0;
   %bestD2 = %maxDist * %maxDist;

   %n = SpawnAnimalSet.getCount();
   for (%i = 0; %i < %n; %i++)
   {
      %an = SpawnAnimalSet.getObject(%i);
      if (!SA_isOurAnimal(%an))
         continue;

      %apos = %an.getPosition();
      %d    = vectorDist(%pos, %apos);
      %d2   = %d * %d;

      if (%d2 < %bestD2)
      {
         %best   = %an;
         %bestD2 = %d2;
      }
   }

   return %best;
}

function SA_killAnimalDelayed(%anId)
{
   %an = %anId;
   if (!isObject(%an) || %an.getClassName() !$= "Animal")
      return;

   if (isObject(SpawnAnimalSet) && SpawnAnimalSet.isMember(%an))
      SpawnAnimalSet.remove(%an);

   %info = SA_name(%an);
   %an.delete();
   echo("[SA:CORPSE:KILL] removed Animal " @ %info @ " (delayed)");
}

function SA_handleCorpseObject(%group, %obj)
{
   if (!isObject(%obj))
      return;

   %cls = %obj.getClassName();
   if (%cls !$= "TSStatic" && %cls !$= "ComplexObject")
      return;

   %pos = (%obj.isMethod("getPosition") ? %obj.getPosition() : "?");
   if (%pos $= "?")
      return;

   %maxMatchDist = 0.5;

   %an = SA_findClosestOurAnimal(%pos, %maxMatchDist);
   if (!isObject(%an))
      return;

   %apos = %an.getPosition();
   %dist = mFloatLength(vectorDist(%pos, %apos), 4);

   echo("[SA:CORPSE:TRIGGER] group=" @ (isObject(%group) ? %group.getName() : "0")
        @ " corpseObj=" @ %obj.getId()
        @ " class=" @ %cls
        @ " pos=" @ %pos
        @ " -> matchAnimal=" @ SA_name(%an)
        @ " dist=" @ %dist);

   schedule(($SA_KILL_DELAY_MS $= "" ? 1000 : $SA_KILL_DELAY_MS),
            0, "SA_killAnimalDelayed", %an);
}

// ============================================================================
// Reguły z config.ini – TYLKO pos=..., max 1 żywy Animal na regułę (prosty wariant)
// ============================================================================

if (!isObject(SpawnAnimalRuleSet))
   new SimSet(SpawnAnimalRuleSet);

function SA_ensureRuleSet()
{
   if (!isObject(SpawnAnimalRuleSet))
      new SimSet(SpawnAnimalRuleSet);
   return SpawnAnimalRuleSet;
}

function SA_makeRule(%count, %dbName, %q, %posStr, %radius, %Tmin)
{
   %r = new ScriptObject(SA_SpawnRule)
   {
      count      = SA_toInt(%count);      // w tej wersji i tak 1, ale parsujemy
      dbName     = trim(%dbName);
      quality    = SA_toInt(%q);
      posStr     = %posStr;               // "x y z"
      radius     = SA_toFloat(%radius);   // 0 -> spawn dokładnie w pos
      periodMin  = SA_toFloat(%Tmin);     // co ile minut sprawdzamy
      lastAnimal = 0;                     // max 1 żywy per rule
   };

   if (%r.count <= 0)
      %r.count = 1;

   SA_ensureRuleSet().add(%r);

   echo("[SA:CFG] rule created count=" @ %r.count
        @ " db=" @ %r.dbName
        @ " q=" @ %r.quality
        @ " pos=" @ %r.posStr
        @ " r=" @ %r.radius
        @ " T=" @ %r.periodMin);

   return %r;
}

function SA_ruleResolvePos(%rule)
{
   if (!isObject(%rule))
      return "";

   return %rule.posStr;
}

function SA_SpawnRule::getAliveCount(%this)
{
   %alive = 0;

   if (isObject(%this.lastAnimal))
   {
      if (%this.lastAnimal.getClassName() $= "Animal")
         %alive = 1;
      else
         %this.lastAnimal = 0;
   }

   return %alive;
}

function SA_SpawnRule::doSpawn(%this)
{
   if (!isObject(%this))
      return;

   %alive = %this.getAliveCount();
   if (%alive >= 1)
   {
      if (%this.periodMin > 0)
         %this.schedule(%this.periodMin * 60 * 1000, "doSpawn");
      return;
   }

   %center = SA_ruleResolvePos(%this);
   if (%center $= "")
   {
      echo("[SA:RULE] cannot resolve pos for rule db=" @ %this.dbName);
      if (%this.periodMin > 0)
         %this.schedule(%this.periodMin * 60 * 1000, "doSpawn");
      return;
   }

   %cx = getWord(%center, 0);
   %cy = getWord(%center, 1);
   %cz = getWord(%center, 2);

   %r = (%this.radius > 0 ? %this.radius : 0);

   if (%r > 0)
   {
      %xy = SA_offset(%cx, %cy, %r * (0.4 + 0.6 * getRandom()));
      %sx = getWord(%xy, 0);
      %sy = getWord(%xy, 1);
   }
   else
   {
      %sx = %cx;
      %sy = %cy;
   }

   %dbName = %this.dbName;
   if (!isObject(%dbName))
   {
      echo("[SA:RULE] invalid datablock " @ %dbName);
      if (%this.periodMin > 0)
         %this.schedule(%this.periodMin * 60 * 1000, "doSpawn");
      return;
   }

   %an = SA_spawnOne(0, %dbName, %this.quality, %sx, %sy, %cz, mFloor(getRandom() * 360));
   if (isObject(%an))
      %this.lastAnimal = %an;

   if (%this.periodMin > 0)
      %this.schedule(%this.periodMin * 60 * 1000, "doSpawn");
}

function SA_loadConfig()
{
   SA_ensureRuleSet();

   while (SpawnAnimalRuleSet.getCount() > 0)
   {
      %r = SpawnAnimalRuleSet.getObject(0);
      SpawnAnimalRuleSet.remove(%r);
      %r.delete();
   }

   %file = $SA_CFG_PATH;
   if (%file $= "")
      %file = "mods/SpawnAnimal/config.ini";

   if (!isFile(%file))
   {
      echo("[SA:CFG] no config file " @ %file);
      return;
   }

   %fh = new FileObject();
   %fh.openForRead(%file);

   %lineNum = 0;
   %rules   = 0;

   while (!%fh.isEOF())
   {
      %line = %fh.readLine();
      %lineNum++;

      %trim = trim(%line);
      if (%trim $= "")
         continue;

      %first = getSubStr(%trim, 0, 1);
      if (%first $= "#" || %first $= ";")
         continue;

      if (getSubStr(%trim, 0, 2) $= "//")
         continue;

      if (getSubStr(%trim, 0, 1) $= "(" &&
          getSubStr(%trim, strlen(%trim) - 1, 1) $= ")")
      {
         %trim = getSubStr(%trim, 1, strlen(%trim) - 2);
      }

      %firstComma = strstr(%trim, ",");
      if (%firstComma == -1)
      {
         echo("[SA:CFG] line " @ %lineNum @ ": invalid syntax -> " @ %line);
         continue;
      }

      %cntStr = trim(getSubStr(%trim, 0, %firstComma));
      %rest1  = getSubStr(%trim, %firstComma + 1, 1000);

      %secondComma = strstr(%rest1, ",");
      if (%secondComma == -1)
      {
         echo("[SA:CFG] line " @ %lineNum @ ": invalid syntax -> " @ %line);
         continue;
      }

      %dbStr = trim(getSubStr(%rest1, 0, %secondComma));
      %rest2 = getSubStr(%rest1, %secondComma + 1, 1000);

      %thirdComma = strstr(%rest2, ",");
      if (%thirdComma == -1)
      {
         %qStr   = trim(%rest2);
         %cfgStr = "";
      }
      else
      {
         %qStr   = trim(getSubStr(%rest2, 0, %thirdComma));
         %cfgStr = getSubStr(%rest2, %thirdComma + 1, 1000);
      }

      if (%cntStr $= "" || %dbStr $= "" || %qStr $= "")
      {
         echo("[SA:CFG] line " @ %lineNum @ ": invalid syntax -> " @ %line);
         continue;
      }

      %posStr = "";
      %radius = 0;
      %T      = 0;

      // pos= X Y Z
      %idxPos = strstr(%cfgStr, "pos=");
      if (%idxPos != -1)
      {
         %afterPos = getSubStr(%cfgStr, %idxPos + 4, 1000);
         %commaPos = strstr(%afterPos, ",");
         if (%commaPos == -1)
            %coords = trim(%afterPos);
         else
            %coords = trim(getSubStr(%afterPos, 0, %commaPos));

         if (getWordCount(%coords) == 3)
            %posStr = %coords;
      }

      // r=R
      %idxR = strstr(%cfgStr, "r=");
      if (%idxR != -1)
      {
         %afterR = getSubStr(%cfgStr, %idxR + 2, 1000);
         %commaR = strstr(%afterR, ",");
         if (%commaR == -1)
            %rStr = trim(%afterR);
         else
            %rStr = trim(getSubStr(%afterR, 0, %commaR));
         %radius = %rStr;
      }

      // T=MINUTES
      %idxT = strstr(%cfgStr, "T=");
      if (%idxT != -1)
      {
         %afterT = getSubStr(%cfgStr, %idxT + 2, 1000);
         %commaT = strstr(%afterT, ",");
         if (%commaT == -1)
            %TStr = trim(%afterT);
         else
            %TStr = trim(getSubStr(%afterT, 0, %commaT));
         %T = %TStr;
      }

      if (%posStr $= "")
      {
         echo("[SA:CFG] line " @ %lineNum @ ": missing pos= for rule -> " @ %line);
         continue;
      }

      %rule = SA_makeRule(%cntStr, %dbStr, %qStr, %posStr, %radius, %T);
      if (isObject(%rule))
         %rules++;
   }

   %fh.close();
   %fh.delete();

   echo("[SA:CFG] loaded rules: " @ %rules);
}

function SA_startAutoSpawns()
{
   SA_ensureRuleSet();

   %n = SpawnAnimalRuleSet.getCount();
   for (%i = 0; %i < %n; %i++)
   {
      %r = SpawnAnimalRuleSet.getObject(%i);
      if (%r.periodMin > 0)
         %r.schedule(1000 + %i * 500, "doSpawn");
   }
}

function SA_reloadConfig()
{
   SA_loadConfig();
   SA_startAutoSpawns();
}

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
};

activatePackage(SpawnAnimalPkg);
echo("[SA] SpawnAnimalPkg activated");
