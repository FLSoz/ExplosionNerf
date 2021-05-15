using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using System.Reflection;
using Harmony;
using UnityEngine;
using System.Collections;


namespace ExplosionNerf
{

    public static class IngressPoint
    {
        public static bool Debug = false;

        private static IEnumerable<DictionaryEntry> CastDict(IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                yield return entry;
            }
        }

        private static void DebugPrint(String prefix, String contents)
        {
            if (ExplosionNerf.IngressPoint.Debug)
            {
                Console.WriteLine(prefix + contents);
            }
        }

        // Get all blocks the explosion affects
        // ASSUMPTION: Explosions are handled sequentially on the main thread. i.e. only one Explosion.Explode() is running at any one time
        [HarmonyPatch(typeof(Explosion))]
        [HarmonyPatch("GatherVisibleHits")]
        public static class PatchExplosion
        {
            private static FieldInfo hitDict = typeof(Explosion).GetField("s_VisibleHits", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            private static IDictionary s_VisibleHits;

            public static void Postfix(ref Explosion __instance)
            {
                ExplosionNerf.IngressPoint.DebugPrint("<ENM> ", "============================ NEW EXPLOSION ============================");
                // ExplosionNerf.IngressPoint.DebugPrint("initial");
                PatchDamage.hitBlock = null;
                PatchDamage.originalDamage = __instance.m_MaxDamageStrength;
                PatchExplosion.s_VisibleHits = (IDictionary)PatchExplosion.hitDict.GetValue(null);
                // ExplosionNerf.IngressPoint.DebugPrint("fetch");
                PatchDamage.castSource = __instance;
                if (PatchExplosion.s_VisibleHits != null && PatchExplosion.s_VisibleHits.Count > 0)
                {
                    Damageable directHit = (Damageable)PatchDamage.DirectHit.GetValue(__instance);
                    // if (directHit != null && directHit.Block != null && (directHit.MaxHealth >= 1.0f || directHit.Block.GetComponent<ModuleShieldGenerator>() == null))
                    if (directHit != null && directHit.Block != null && directHit.Block.tank != null && directHit.Block.visible.damageable == directHit)
                    {
                        PatchDamage.hitBlock = directHit.Block;
                    }

                    // ExplosionNerf.IngressPoint.DebugPrint("condition");
                    Dictionary<int, object> newDictionary = CastDict(PatchExplosion.s_VisibleHits).ToDictionary(entry => (int)entry.Key, entry => entry.Value);
                    // ExplosionNerf.IngressPoint.DebugPrint("cast");
                    List<object> myList = newDictionary.Values.ToList();

                    PatchDamage.YetToHit.Clear();
                    foreach (HashSet<TankBlock> table in PatchDamage.DeterminedInvincible.Values)
                    {
                        table.Clear();
                    }
                    PatchDamage.DeterminedInvincible.Clear();
                    foreach (object obj in myList)
                    {
                        Visible visible = (Visible) obj.GetType().GetField("visible", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).GetValue(obj);
                        if (visible != null)
                        {
                            TankBlock tentative = visible.block;
                            if (tentative != null)
                            {
                                PatchDamage.YetToHit.Add(tentative);
                            }
                        }
                    }
                }
            }
        }

        // Do the LOS/recursion on explode
        [HarmonyPatch(typeof(ManDamage))]
        [HarmonyPatch("DealDamage")]
        [HarmonyPatch(new Type[] { typeof(Damageable), typeof(float), typeof(ManDamage.DamageType), typeof(Component), typeof(Tank), typeof(Vector3), typeof(Vector3), typeof(float), typeof(float) })]
        public static class PatchDamage
        {
            public static Explosion castSource;
            public static float originalDamage;
            public static TankBlock hitBlock;
            public static HashSet<TankBlock> YetToHit = new HashSet<TankBlock>();
            public static Dictionary<Tank, HashSet<TankBlock>> DeterminedInvincible = new Dictionary<Tank, HashSet<TankBlock>>();

            public static FieldInfo DirectHit = typeof(Explosion).GetField("m_DirectHitTarget", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            private static FieldInfo m_AoEDamageBlockPercent = typeof(Damageable).GetField("m_AoEDamageBlockPercent", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            private static FieldInfo m_DamageType = typeof(Explosion).GetField("m_DamageType", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            private static FieldInfo m_DamageSource = typeof(Explosion).GetField("m_DamageSource", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            private static FieldInfo m_ExplodeCountdownTimer = typeof(ModuleDamage).GetField("m_ExplodeCountdownTimer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            // private static FieldInfo rejectDamageEvent = typeof(Damageable).GetField("rejectDamageEvent", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            private static FieldInfo m_DamageMultiplierTable = typeof(ManDamage).GetField("m_DamageMultiplierTable", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            private static FieldInfo m_CabDamageDissipationFactor = typeof(ManDamage).GetField("m_CabDamageDissipationFactor", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            private static FieldInfo m_CabDamageDissipationDetachFactor = typeof(ManDamage).GetField("m_CabDamageDissipationDetachFactor", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            private static FieldInfo s_AdjacentBlocks = typeof(ManDamage).GetField("s_AdjacentBlocks", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

            public static bool Prefix(ref ManDamage __instance, ref float __result, ref float damage, ref ManDamage.DamageType damageType, ref Damageable damageTarget, ref Component source, ref Tank sourceTank, ref Vector3 hitPosition, ref Vector3 damageDirection, ref float kickbackStrength, ref float kickbackDuration)
            {
                // ExplosionNerf.IngressPoint.DebugPrint("ASDF");
                if (sourceTank != null && hitPosition != default && damageDirection != default)
                {
                    TankBlock targetBlock = damageTarget.Block;
                    // ExplosionNerf.IngressPoint.DebugPrint("<ENM> ", "Check 1");
                    if (targetBlock != null && source != null && source.GetType() == typeof(Explosion))
                    {
                        // ExplosionNerf.IngressPoint.DebugPrint("<ENM> ", "Check 2");
                        Damageable directHit = PatchDamage.hitBlock == null ? null : (Damageable) PatchDamage.DirectHit.GetValue(source);
                        Tank targetTank = targetBlock.tank;
                        if (targetTank != null && targetTank != sourceTank && targetTank.Team != sourceTank.Team)
                        {
                            // ExplosionNerf.IngressPoint.DebugPrint("<ENM> ", "Check 3");
                            // ManDamage.DamageInfo damageInfo = new ManDamage.DamageInfo(damage, damageType, source, sourceTank, hitPosition, damageDirection, kickbackStrength, kickbackDuration);
                            __result = recursiveHandleDamage(ref __instance, ref __result, damageTarget, directHit, source, targetBlock, targetTank, "<ENM> ");
                            return false;
                        }
                    }
                }
                return true;
            }

            private static ManDamage.DamageInfo NewDamageInfo(Damageable directHit, Damageable damageTarget, TankBlock targetBlock)
            {
                Vector3 actual = PatchDamage.castSource.transform.position - targetBlock.transform.position;
                // Console.WriteLine("1");
                float a = (float)(1.0 / ((double)PatchDamage.castSource.m_EffectRadius * (double)PatchDamage.castSource.m_EffectRadius));
                // Console.WriteLine("2");
                float b = (float)(1.0 / ((double)PatchDamage.castSource.m_EffectRadiusMaxStrength * (double)PatchDamage.castSource.m_EffectRadiusMaxStrength));
                // Console.WriteLine("3");
                float sqDist = actual.sqrMagnitude;
                // Console.WriteLine("4");
                float num1 = !((UnityEngine.Object)directHit != (UnityEngine.Object)null) || ((UnityEngine.Object)damageTarget == (UnityEngine.Object)directHit) ? Mathf.InverseLerp(a, b, 1f / sqDist) : 1f;
                // Console.WriteLine("5");
                float damage = PatchDamage.castSource.DoDamage ? PatchDamage.castSource.m_MaxDamageStrength * num1 : 0.0f;
                // Console.WriteLine("6");
                Vector3 damageDirection = (- actual).normalized * num1 * PatchDamage.castSource.m_MaxImpulseStrength;
                // Console.WriteLine("7");
                damage *= (0.5f + (float)targetBlock.GetComponent<ModuleDamage>().m_DamageDetachFragility);
                return new ManDamage.DamageInfo(damage, (ManDamage.DamageType) PatchDamage.m_DamageType.GetValue(PatchDamage.castSource), (Component) PatchDamage.castSource, (Tank) PatchDamage.m_DamageSource.GetValue(PatchDamage.castSource), targetBlock.transform.position, damageDirection, 0.0f, 0.0f);
            }

            private static float recursiveHandleDamage(ref ManDamage __instance, ref float __result, Damageable damageTarget, Damageable directHit, Component source, TankBlock targetBlock, Tank targetTank, string prefix, bool RecurseOverride = false)
            {
                if (RecurseOverride || PatchDamage.YetToHit.Contains(targetBlock)) {

                    ExplosionNerf.IngressPoint.DebugPrint("\n" + prefix, "Resolving Block: " + targetBlock.name);
                    PatchDamage.YetToHit.Remove(targetBlock);

                    if (directHit != null)
                    {
                        ExplosionNerf.IngressPoint.DebugPrint(prefix, "direct hit detected");
                        TankBlock localHitBlock = directHit.Block;
                        if (localHitBlock != PatchDamage.hitBlock)
                        {
                            ExplosionNerf.IngressPoint.DebugPrint(prefix, "ONLY should happen when direct hit RESOLVED, block NOT destroyed");
                            ExplosionNerf.IngressPoint.DebugPrint(prefix, "RESOLUTION - ALWAYS BLOCK");
                            return 0.0f;
                        }

                        if (directHit == damageTarget)
                        {
                            // do actual dmg to targetBlock
                            ExplosionNerf.IngressPoint.DebugPrint(prefix, "Direct hit NEVER invincible - calculate dmg");

                            Explosion castSource = (Explosion)source;

                            // Console.WriteLine(directHit.MaxHealth <= 1.0f);
                            // Console.WriteLine(localHitBlock.GetComponent<ModuleShieldGenerator>() != null);
                            // Console.WriteLine(PatchDamage.rejectDamageEvent.GetValue());

                            ManDamage.DamageInfo damageInfo = PatchDamage.NewDamageInfo(directHit, damageTarget, targetBlock);
                            ExplosionNerf.IngressPoint.DebugPrint(prefix, "Damage: " + damageInfo.Damage.ToString());
                            float dmgDone = PatchDamage.DoDamage(ref __instance, directHit, damageTarget, damageInfo);
                            if (dmgDone == 0.0f)
                            {
                                ExplosionNerf.IngressPoint.DebugPrint(prefix, "Block NOT destroyed - mark invincible for now");
                                if (!PatchDamage.DeterminedInvincible.ContainsKey(targetTank))
                                {
                                    PatchDamage.DeterminedInvincible[targetTank] = new HashSet<TankBlock>();
                                }
                                PatchDamage.DeterminedInvincible[targetTank].Add(localHitBlock);
                            }
                            else
                            {
                                ExplosionNerf.IngressPoint.DebugPrint(prefix, "BLOCK DESTROYED - remove from consideration");
                                PatchDamage.DirectHit.SetValue(source, null);
                            }
                            PatchDamage.hitBlock = null;

                            // modify damageInfo for recursion purposes - no longer necessary
                            // damageInfo.ApplyDamageMultiplier(newDamageMult);

                            // modify dmg energy left in explosion
                            castSource.m_MaxDamageStrength *= (float)PatchDamage.m_AoEDamageBlockPercent.GetValue(directHit);
                            // PatchDamage.DirectHit.SetValue(source, null);
                            return dmgDone;
                        }
                        else
                        {
                            ExplosionNerf.IngressPoint.DebugPrint(prefix, "Resolve Direct hit first");
                            float destroyed = PatchDamage.recursiveHandleDamage(ref __instance, ref __result, directHit, directHit, source, localHitBlock, targetTank, prefix + "|  ", true);

                            // if block was actually destroyed
                            if (destroyed != 0.0f)
                            {
                                // do actual dmg to targetBlock
                                PatchDamage.hitBlock = null;
                                ExplosionNerf.IngressPoint.DebugPrint(prefix, "Resolve NOT NECESSARILY invincible - calculate dmg (re-recurse)");
                                PatchDamage.YetToHit.Add(targetBlock);
                                directHit = (Damageable)PatchDamage.DirectHit.GetValue(source);
                                return PatchDamage.recursiveHandleDamage(ref __instance, ref __result, damageTarget, directHit, source, targetBlock, targetTank, prefix + "|  ");
                            }
                            else
                            {
                                ExplosionNerf.IngressPoint.DebugPrint(prefix, "ONLY should happen when direct hit RESOLVED, block NOT destroyed");
                                ExplosionNerf.IngressPoint.DebugPrint(prefix, "RESOLUTION - ALWAYS BLOCK");
                                if (!PatchDamage.DeterminedInvincible.ContainsKey(targetTank))
                                {
                                    PatchDamage.DeterminedInvincible[targetTank] = new HashSet<TankBlock>();
                                }
                                PatchDamage.DeterminedInvincible[targetTank].Add(targetBlock);
                                return 0.0f;
                            }
                        }
                    }
                    else
                    {
                        ExplosionNerf.IngressPoint.DebugPrint(prefix, "no direct hit - Raycast time");
                        Vector3 actual = source.transform.position - targetBlock.transform.position;
                        RaycastHit[] results = new RaycastHit[((int)actual.magnitude)];
                        int hits = Physics.RaycastNonAlloc(new Ray(targetBlock.transform.position, actual), results, actual.magnitude, Singleton.Manager<ManVisible>.inst.VisiblePickerMaskNoTechs, QueryTriggerInteraction.Ignore);

                        List<TankBlock> foundBlocks = new List<TankBlock>();
                        if (!PatchDamage.DeterminedInvincible.ContainsKey(targetTank))
                        {
                            PatchDamage.DeterminedInvincible[targetTank] = new HashSet<TankBlock>();
                        }
                        for (int i = 0; i < hits; i++)
                        {
                            // ExplosionNerf.IngressPoint.DebugPrint("Loop " + i.ToString());
                            RaycastHit test = results[i];
                            Visible visible = Visible.FindVisibleUpwards((Component)test.collider);
                            if ((UnityEngine.Object)visible != (UnityEngine.Object)null)
                            {
                                TankBlock block = visible.block;
                                if (block != null && block != targetBlock && block.tank != null)
                                {
                                    if (block.tank == targetTank)
                                    {
                                        // ExplosionNerf.IngressPoint.DebugPrint("Damage blocked");
                                        if (PatchDamage.YetToHit.Contains(block))
                                        {
                                            ExplosionNerf.IngressPoint.DebugPrint(prefix, "  Unresolved block found: " + block.name);
                                            foundBlocks.Add(block);
                                        }
                                        else if (PatchDamage.DeterminedInvincible[targetTank].Contains(block))
                                        {
                                            ExplosionNerf.IngressPoint.DebugPrint(prefix, "  Resolved INVINCIBLE found");
                                            PatchDamage.DeterminedInvincible[targetTank].Add(targetBlock);
                                            foundBlocks.Clear();
                                            return 0.0f;
                                        }
                                    }
                                }
                            }
                        }
                        ExplosionNerf.IngressPoint.DebugPrint(prefix, "Raycast initial pass done - no invincible found in path");
                        if (foundBlocks.Count > 0)
                        {
                            bool breakInvincible = false;
                            foreach (TankBlock block in foundBlocks)
                            {
                                if (PatchDamage.YetToHit.Contains(block))
                                {
                                    float destroyed = PatchDamage.recursiveHandleDamage(ref __instance, ref __result, block.GetComponent<Damageable>(), directHit, source, block, targetTank, prefix + "|  ");

                                    if (destroyed == 0.0f)
                                    {
                                        PatchDamage.DeterminedInvincible[targetTank].Add(targetBlock);

                                        breakInvincible = true;
                                        break;
                                    }
                                }
                                else if (PatchDamage.DeterminedInvincible[targetTank].Contains(block))
                                {
                                    PatchDamage.DeterminedInvincible[targetTank].Add(targetBlock);

                                    breakInvincible = true;
                                    ExplosionNerf.IngressPoint.DebugPrint(prefix, "Resolved INVINCIBLE found");
                                    break;
                                }
                            }

                            if (!breakInvincible)
                            {
                                ExplosionNerf.IngressPoint.DebugPrint(prefix, "(A) [Should be rare] Resolve NOT invincible - calculate dmg");
                                ManDamage.DamageInfo damageInfo = PatchDamage.NewDamageInfo(directHit, damageTarget, targetBlock);
                                ExplosionNerf.IngressPoint.DebugPrint(prefix, "Damage: " + damageInfo.Damage.ToString());
                                float dmgDone = PatchDamage.DoDamage(ref __instance, directHit, damageTarget, damageInfo);
                                if (dmgDone == 0.0f)
                                {
                                    ExplosionNerf.IngressPoint.DebugPrint(prefix, "Block NOT destroyed - mark invincible for now");
                                    if (!PatchDamage.DeterminedInvincible.ContainsKey(targetTank))
                                    {
                                        PatchDamage.DeterminedInvincible[targetTank] = new HashSet<TankBlock>();
                                    }
                                    PatchDamage.DeterminedInvincible[targetTank].Add(targetBlock);
                                }
                                else
                                {
                                    ExplosionNerf.IngressPoint.DebugPrint(prefix, "BLOCK DESTROYED - remove from consideration");
                                }
                                return dmgDone;
                            }
                            else
                            {
                                ExplosionNerf.IngressPoint.DebugPrint(prefix, "Resolved INVINCIBLE found");
                                return 0.0f;
                            }
                        }
                        else
                        {
                            ExplosionNerf.IngressPoint.DebugPrint(prefix, "No blocks in way - Resolve NOT invincible - calculate dmg");
                            ManDamage.DamageInfo damageInfo = PatchDamage.NewDamageInfo(directHit, damageTarget, targetBlock);
                            ExplosionNerf.IngressPoint.DebugPrint(prefix, "Damage: " + damageInfo.Damage.ToString());
                            float dmgDone = PatchDamage.DoDamage(ref __instance, directHit, damageTarget, damageInfo);
                            if (dmgDone == 0.0f)
                            {
                                ExplosionNerf.IngressPoint.DebugPrint(prefix, "Block NOT destroyed - mark invincible for now");
                                if (!PatchDamage.DeterminedInvincible.ContainsKey(targetTank))
                                {
                                    PatchDamage.DeterminedInvincible[targetTank] = new HashSet<TankBlock>();
                                }
                                PatchDamage.DeterminedInvincible[targetTank].Add(targetBlock);
                            }
                            else
                            {
                                ExplosionNerf.IngressPoint.DebugPrint(prefix, "BLOCK DESTROYED - remove from consideration");
                            }
                            return dmgDone;
                        }
                    }
                }
                ExplosionNerf.IngressPoint.DebugPrint(prefix, "END");
                return 0.0f;
            }

            private static float  DoDamage(ref ManDamage __instance, Damageable directHit, Damageable damageTarget, ManDamage.DamageInfo damageInfo)
            {
                ExplosionNerf.IngressPoint.DebugPrint("<ENM> ", "Doing DMG");

                float cabDamageDissipationFactor = (float)PatchDamage.m_CabDamageDissipationFactor.GetValue(__instance);
                // ExplosionNerf.IngressPoint.DebugPrint("A");

                float dmgDone = 0.0f;

                ManDamage.DamageInfo damageInfo1 = damageInfo.Clone();

                if (directHit != null)
                {
                    if ((UnityEngine.Object)PatchDamage.m_DamageMultiplierTable.GetValue(__instance) != (UnityEngine.Object)null)
                    {
                        // ExplosionNerf.IngressPoint.DebugPrint("B1");
                        float damageMultiplier = ((DamageMultiplierTable)PatchDamage.m_DamageMultiplierTable.GetValue(__instance)).GetDamageMultiplier(damageInfo.DamageType, directHit.DamageableType, true);
                        // ExplosionNerf.IngressPoint.DebugPrint("B2");
                        if ((double)damageMultiplier != 1.0)
                            damageInfo1.ApplyDamageMultiplier(damageMultiplier);
                        // ExplosionNerf.IngressPoint.DebugPrint("B3");
                    }

                    TankBlock hitBlock = damageTarget.Block;
                    if (hitBlock != null)
                    {
                        damageInfo1.ApplyDamageMultiplier(Mathf.Pow(hitBlock.filledCells.Length, 2.0f / 3.0f));
                    }

                    // ExplosionNerf.IngressPoint.DebugPrint("D");
                    int adjCount = 0;
                    if ((double)cabDamageDissipationFactor > 0.0 && directHit.Block.IsNotNull() && directHit.Block.IsController)
                    {
                        // ExplosionNerf.IngressPoint.DebugPrint("E1");
                        adjCount = directHit.Block.ConnectedBlocksByAP.Count();
                    }

                    if (adjCount > 0)
                    {
                        // ExplosionNerf.IngressPoint.DebugPrint("F1");
                        float cabDamageDissipationDetachFactor = (float)PatchDamage.m_CabDamageDissipationDetachFactor.GetValue(__instance);
                        // ExplosionNerf.IngressPoint.DebugPrint("F2");
                        // ManDamage.DamageInfo damageInfo2 = damageInfo1.Clone();
                        float multiplier = cabDamageDissipationFactor / (float)(adjCount + 1);
                        damageInfo1.ApplyDamageMultiplier((float)(1.0 - (double)multiplier * (double)adjCount));
                    }
                }
                dmgDone = damageTarget.TryToDamage(damageInfo1, true);

                //  || damageTarget.Block == null || damageTarget.Block.tank == null
                if (dmgDone == 0.0)
                {
                    if (damageTarget == null || damageTarget.Health <= 0.0f || damageTarget.Block == null || damageTarget.Block.PreExplodePulse || damageTarget.Block.tank == null)
                    {
                        dmgDone = 1.0f;
                    }
                    else
                    {
                        ModuleDamage module = damageTarget.Block.GetComponent<ModuleDamage>();
                        if (module != null)
                        {
                            dmgDone = (float)PatchDamage.m_ExplodeCountdownTimer.GetValue(module);
                        }
                    }
                }
                ExplosionNerf.IngressPoint.DebugPrint("<ENM> ", "Dmg Done: " + dmgDone.ToString());

                return dmgDone;
            }
        }

        // Restore explosion damage to original after explosion
        [HarmonyPatch(typeof(Explosion), "Explode")]
        public static class PatchExplosionDmg
        {
            public static void Postfix(ref Explosion __instance)
            {
                if (__instance == PatchDamage.castSource)
                {
                    __instance.m_MaxDamageStrength = PatchDamage.originalDamage;
                }
                else
                {
                    Console.WriteLine("<ENM> ERROR: EXPLOSIONS NOT EQUAL");
                }
            }
        }

        // Do the high damage penetration bits
        [HarmonyPatch(typeof(Projectile))]
        [HarmonyPatch("OnCollisionEnter")]
        public static class PatchProjectile
        {
            private static FieldInfo m_Damage = typeof(Projectile).GetField("m_Damage", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            private static FieldInfo m_DamageType = typeof(Projectile).GetField("m_DamageType", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            private static FieldInfo m_Stuck = typeof(Projectile).GetField("m_Stuck", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            private static FieldInfo m_SingleImpact = typeof(Projectile).GetField("m_SingleImpact", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            private static FieldInfo m_HasSetCollisionDeathDelay = typeof(Projectile).GetField("m_HasSetCollisionDeathDelay", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            private static FieldInfo m_Weapon = typeof(Projectile).GetField("m_Weapon", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            private static FieldInfo m_StickOnContact = typeof(Projectile).GetField("m_StickOnContact", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            private static FieldInfo m_ExplodeOnStick = typeof(Projectile).GetField("m_ExplodeOnStick", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            private static FieldInfo m_VisibleStuckTo = typeof(Projectile).GetField("m_VisibleStuckTo", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            private static FieldInfo m_Smoke = typeof(Projectile).GetField("m_Smoke", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            private static FieldInfo m_ExplodeOnTerrain = typeof(Projectile).GetField("m_ExplodeOnTerrain", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            private static FieldInfo m_StickOnTerrain = typeof(Projectile).GetField("m_StickOnTerrain", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            private static FieldInfo OnParentDestroyed = typeof(Projectile).GetField("OnParentDestroyed", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            private static FieldInfo m_StickImpactEffect = typeof(Projectile).GetField("m_StickImpactEffect", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            private static FieldInfo m_ImpactSFXType = typeof(Projectile).GetField("m_ImpactSFXType", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            private static FieldInfo m_SeekingProjectile = typeof(Projectile).GetField("m_SeekingProjectile", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            private static MethodInfo IsProjectileArmed = typeof(Projectile).GetMethod("IsProjectileArmed", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            private static MethodInfo SpawnExplosion = typeof(Projectile).GetMethod("SpawnExplosion", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            private static MethodInfo SpawnStickImpactEffect = typeof(Projectile).GetMethod("SpawnStickImpactEffect", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            private static MethodInfo SpawnTerrainHitEffect = typeof(Projectile).GetMethod("SpawnTerrainHitEffect", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            private static MethodInfo SetStuck = typeof(Projectile).GetMethod("SetStuck", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            private static MethodInfo GetDeathDelay = typeof(Projectile).GetMethod("GetDeathDelay", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            private static MethodInfo OnDelayedDeathSet = typeof(Projectile).GetMethod("OnDelayedDeathSet", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            private static MethodInfo SetProjectileForDelayedDestruction = typeof(Projectile).GetMethod("SetProjectileForDelayedDestruction", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);


            private static int HandleCollision(Projectile __instance, Damageable damageable, Vector3 hitPoint, Collider otherCollider, bool ForceDestroy)
            {
                DebugPrint("[ENF-AP] ", "Stage 1");
                int retVal = 0;
                if (!((Component) __instance).gameObject.activeInHierarchy)
                {
                    return 0;
                }
                DebugPrint("[ENF-AP] ", "Stage 2");
                if ((bool) PatchProjectile.m_Stuck.GetValue(__instance))
                {
                    return 0;
                }
                DebugPrint("[ENF-AP] ", "Stage 3");
                bool singleImpact = (bool)PatchProjectile.m_SingleImpact.GetValue(__instance);
                /* if ((bool) PatchProjectile.m_SingleImpact.GetValue(__instance) && (bool) PatchProjectile.m_HasSetCollisionDeathDelay.GetValue(__instance))
                {
                    return 0;
                } */
                bool flag = false;
                DebugPrint("[ENF-AP] ", "Stage 4");

                bool stickOnContact = (bool)PatchProjectile.m_StickOnContact.GetValue(__instance);
                float deathDelay = (float)PatchProjectile.GetDeathDelay.Invoke(__instance, null);

                if (damageable)
                {
                    ManDamage.DamageInfo damageInfo = new ManDamage.DamageInfo((float)(int)PatchProjectile.m_Damage.GetValue(__instance), (ManDamage.DamageType)PatchProjectile.m_DamageType.GetValue(__instance), (ModuleWeapon)PatchProjectile.m_Weapon.GetValue(__instance), __instance.Shooter, hitPoint, __instance.rbody.velocity, 0f, 0f);
                    float damageDealt = Singleton.Manager<ManDamage>.inst.DealDamage(damageInfo, damageable);
                    float damage = (float) (int) PatchProjectile.m_Damage.GetValue(__instance);

                    DebugPrint("[ENF-AP] ", "Stage 4a");
                    DebugPrint("[ENF-AP] ", damageDealt.ToString());
                    DebugPrint("[ENF-AP] ", damage.ToString());

                    // block was destroyed, and there is damage leftover
                    if (damageDealt > 0.0f)
                    {
                        retVal = (int) (damage * damageDealt);
                    }
                    else if (deathDelay != 0.0f && !stickOnContact)
                    {
                        // penetration fuse, but failed to kill = flattened, spawn the explosion now
                        deathDelay = 0.0f;
                        PatchProjectile.SpawnExplosion.Invoke(__instance, new object[] { hitPoint, damageable });
                    }
                    else if ((bool)PatchProjectile.IsProjectileArmed.Invoke(__instance, null) && !stickOnContact)
                    {
                        // no penetration fuse, check if armed and not stick on contact - stick on contact explosions are done later
                        PatchProjectile.SpawnExplosion.Invoke(__instance, new object[] { hitPoint, damageable });
                    }
                }
                else if (otherCollider is TerrainCollider || otherCollider.gameObject.layer == Globals.inst.layerLandmark || otherCollider.GetComponentInParents<TerrainObject>(true))
                {
                    DebugPrint("[ENF-AP] ", "Stage 4b");
                    flag = true;
                    PatchProjectile.SpawnTerrainHitEffect.Invoke(__instance, new object[] { hitPoint });
                    DebugPrint("[ENF-AP] ", "Stage 4bb");

                    // if explode on terrain, explode and end, no matter death delay
                    if ((bool)PatchProjectile.m_ExplodeOnTerrain.GetValue(__instance) && (bool) PatchProjectile.IsProjectileArmed.Invoke(__instance, null))
                    {
                        PatchProjectile.SpawnExplosion.Invoke(__instance, new object[] { hitPoint, null });

                        // if default single impact behavior, explode on terrain, then die.
                        // else, keep the bouncing explosions
                        if (singleImpact)
                        {
                            __instance.Recycle(false);
                            return 0;
                        }
                    }
                }
                
                DebugPrint("[ENF-AP] ", "Stage 5");
                Singleton.Manager<ManSFX>.inst.PlayImpactSFX(__instance.Shooter, (ManSFX.WeaponImpactSfxType) PatchProjectile.m_ImpactSFXType.GetValue(__instance), damageable, hitPoint, otherCollider);
                if (stickOnContact && ((bool)PatchProjectile.m_StickOnTerrain.GetValue(__instance) || !flag))
                {
                    DebugPrint("[ENF-AP] ", "Stage 5a");
                    ((Component)__instance).transform.SetParent(otherCollider.gameObject.transform);
                    PatchProjectile.SetStuck.Invoke(__instance, new object[] { true });
                    SmokeTrail smoke = (SmokeTrail)PatchProjectile.m_Smoke.GetValue(__instance);
                    if (smoke)
                    {
                        smoke.enabled = false;
                        smoke.Reset();
                    }

                    DebugPrint("[ENF-AP] ", "Stage 5b");
                    Visible stuckTo = Singleton.Manager<ManVisible>.inst.FindVisible(otherCollider);
                    PatchProjectile.m_VisibleStuckTo.SetValue(__instance, stuckTo);
                    if (stuckTo.IsNotNull())
                    {
                        stuckTo.RecycledEvent.Subscribe(new Action<Visible>((Action<Visible>) PatchProjectile.OnParentDestroyed.GetValue(__instance)));
                    }
                    DebugPrint("[ENF-AP] ", "Stage 5c");
                    if ((bool) PatchProjectile.m_ExplodeOnStick.GetValue(__instance))
                    {
                        Visible visible = (Visible) PatchProjectile.m_VisibleStuckTo.GetValue(__instance);
                        Damageable directHitTarget = visible.IsNotNull() ? visible.damageable : null;
                        PatchProjectile.SpawnExplosion.Invoke(__instance, new object[] { hitPoint, directHitTarget });
                    }
                    DebugPrint("[ENF-AP] ", "Stage 5d");
                    if (((Transform) PatchProjectile.m_StickImpactEffect.GetValue(__instance)).IsNotNull())
                    {
                        PatchProjectile.SpawnStickImpactEffect.Invoke(__instance, new object[] { hitPoint });
                    }
                }

                DebugPrint("[ENF-AP] ", "Stage 6");
                // if here, then no stick on contact, and no damage is leftover, so start destruction sequence
                if (ForceDestroy)   // if projectile hits a shield, always destroy
                {
                    __instance.Recycle(false);
                }
                else if (deathDelay == 0f)
                {
                    if (!flag && retVal > 0)
                    {
                        return retVal;
                    }
                    __instance.Recycle(false);
                }
                else if (!(bool)PatchProjectile.m_HasSetCollisionDeathDelay.GetValue(__instance))
                {
                    PatchProjectile.m_HasSetCollisionDeathDelay.SetValue(__instance, true);
                    PatchProjectile.SetProjectileForDelayedDestruction.Invoke(__instance, new object[] { deathDelay });
                    SeekingProjectile seekingProjectile = (SeekingProjectile)PatchProjectile.m_SeekingProjectile.GetValue(__instance);
                    if (seekingProjectile)
                    {
                        seekingProjectile.enabled = false;
                    }
                    PatchProjectile.OnDelayedDeathSet.Invoke(__instance, null);
                }
                return retVal;
            }

            public static bool Prefix(ref Projectile __instance, ref Collision collision)
            {
                if (__instance.GetType() != typeof(Projectile) || __instance.GetType().IsSubclassOf(typeof(Projectile)))
                {
                    return true;
                }
                if (collision.contactCount == 0)
                {
                    return false;
                }
                ContactPoint contactPoint = collision.GetContact(0);
                int remainderDamage = PatchProjectile.HandleCollision(__instance, contactPoint.otherCollider.GetComponentInParents<Damageable>(true), contactPoint.point, collision.collider, false);
                // if returns 0, then standard behavior, has hit the limit.
                if (remainderDamage > 0)
                {
                    // else, block is destroyed. Decrease damage accordingly, reset relative velocity
                    PatchProjectile.m_Damage.SetValue(__instance, remainderDamage);
                    Vector3 relativeVelocity = collision.relativeVelocity;
                    Rigidbody targetRigidbody = collision.collider.attachedRigidbody;
                    Vector3 targetVelocity = Vector3.zero;
                    if (targetRigidbody)
                    {
                        targetVelocity = targetRigidbody.velocity;
                    }
                    DebugPrint("[ENF-AP] ", relativeVelocity.ToString());
                    DebugPrint("[ENF-AP] ", targetVelocity.ToString());
                    DebugPrint("[ENF-AP] ", __instance.rbody.velocity.ToString());

                    Vector3 originalVelocity = targetVelocity - relativeVelocity;
                    __instance.rbody.velocity = originalVelocity;
                }
                return false;
            }
        }

        // Disable colliders if block destroyed
        [HarmonyPatch(typeof(Damageable))]
        [HarmonyPatch("TryToDamage")]
        public static class PatchDamageable
        {
            public static void Postfix(ref Damageable __instance, ref float __result) {
                if (__result != 0.0f)
                {
                    // block destroyed
                    TankBlock block = __instance.Block;
                    if (block)
                    {
                        Collider[] colliders = block.GetComponentsInChildren<Collider>();
                        foreach (Collider collider in colliders)
                        {
                            collider.enabled = false;
                        }
                    }
                }
                return;
            }
        }

        // disable healing of dying blocks
        [HarmonyPatch(typeof(Damageable))]
        [HarmonyPatch("Repair")]
        public static class PatchHealing
        {
            public static bool Prefix(ref Damageable __instance)
            {
                if (__instance.Health <= 0.0f)
                {
                    return false;
                }
                return true;
            }
        }

        public static void Main()
        {
            HarmonyInstance.Create("flsoz.ttmm.explosionnerf.mod").PatchAll(Assembly.GetExecutingAssembly());
        }
    }
}
