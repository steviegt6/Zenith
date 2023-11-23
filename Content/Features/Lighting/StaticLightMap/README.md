# Optimisation: Static Light Map

Methods such as `Terraria.Lighting::GetColor` and `Terraria.Lighting::GetColor9Slice` are called many, many times per frame and take up a noticeable fraction of frametime.

This can be mostly circumvented through the elimination of virtualization.
