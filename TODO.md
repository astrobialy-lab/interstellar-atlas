# TODO:

- redownload whole gaia data. there is a missing block

- check the requirements to publish the app.
- add release on git for my version with + Data folder + apk + windows + mac + linux.

- add pictures both to github and vr appstore


- new lod:
  advantages:
  - solves the neighbors stars problems (cuz now all filtered by same brightness, and not just local top stars)
  - brightness is spreaded equally
  - i can easily jump between top brightest stars
  disadvantages:
  - cant adjust graphics gently. whether jumps between number of stars are big, or make small jumps but it takes a lot of disk space.
  notes:
  - i can still render more detailed near the player and less far, by jumping between levels of the nodes depending on the distance from the player, instead of choosing the node size by the apparent angle.
  but i choose equal because it more fits for my project.

# BONUS:

- add auto brightness feature: when getting close to a bright star the whole screen is white, it blinds the player.
  make the brightness automatically adjust

- maybe check if i can make the stars look less pixelated
- for stars that are close, i can see the 2x2 pixel glow effect which is ugly

- the dust sprites glitch through the clusters sprites. maybe fix it