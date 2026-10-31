# TODO:

- redownload whole gaia data. there is a missing block

- check the requirements to publish the app.
- add release on git for my version with + Data folder + apk + windows + mac + linux.

- move credits to outside file, keep readme simple
- add instructions how to clone the repo, and where to get the data folder (+ tell about Releases)

- download the Dust data from website, use distance 1.2 kpc, use their script to convert spherical to cubic.
- if some problems, ask yonatan
- thei multiplied the data by 1700 to get the units to hydrogen per volume. ask chatgpt check if i need it (original units are disctinction?)
- the landmarks have a range on their website, compare the ranges with the dust on the map
- adjust the rest of the data to use 1,250 pc from the sun same as the dust. (both download and process)
- update the images on the github
- make sure the dust x,y,z is fine.

- ask shmuel to check if left/right of bachround image are correct. (not axis flipped)

- check the stars distance of the Gaia folder (not Gaia3)

- when switching between stars levels, make so that one of the stages removes all, both stars and clusters.

- recheck all positions:
- stars
- landmarks
- dust
- galactic center + background
- clusters

# BONUS:

- add auto brightness feature: when getting close to a bright star the whole screen is white, it blinds the player.
  make the brightness automatically adjust

- maybe check if i can make the stars look less pixelated
- for stars that are close, i can see the 2x2 pixel glow effect which is ugly

- the dust sprites glitch through the clusters sprites. maybe fix it