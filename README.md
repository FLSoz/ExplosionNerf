# ExplosionNerf
Rebalances explosions.

Explosions no longer pierce and deal damage to blocks not visible from the explosion origin - now blocks need a clear line of sight. Note, that this is calculated by raycasting, so partially obscured blocks and corner blocks will not be damaged by explosions.
Explosion damage will continue through blocks if they are destroyed: i.e. if a block is killed by the explosion, then it will not block line of sight to the blocks behind it.
To compensate, explosion damage is now magnified by block fragility (determines how much damage it can take before it detaches), with block fragility of 0 taking 0.5x damage, and block fragility of 1 taking 1.5x damage.
Explosion damage now also scales roughly by a block's surface area, calculated via (# of block cells)^(2/3)

Armor Piercing projectiles are added (by default). Projectiles will have their effective health decreased after they hit a block. If the block they hit is killed, they will "pierce" through, and continue hitting blocks until they fail to kill a block. At this point, they will explode.
Projectile damage will decrease proportionally to the health of the destroyed blocks (for future block impacts).
This behavior will not happen if it's a sticky projectile.
You can set (modders) custom projectiles with a "time after impact" timer by setting the m_DeathDelay field.

As a consequence of enabling armor piercing projectiles, destroyed blocks will have their colliders disabled, so any and all objects are able to phase through them.
To prevent the case where destroyed blocks are healed, but stuff can still phase through, dead blocks are no longer able to "resurrect" by being within a healing bubble.
(dead meaning flashing red about to detonate, but not yet detonated)
