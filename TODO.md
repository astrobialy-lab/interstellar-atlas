- clean shader after downloading all of the gaia data
- clean star loader
- and verify no trash in the index loader





- get his script that converts to csv of data. and check the units of x,y,z. make sure i use same scales for the stars

- ask for his scripts that process dust and pull + process landmarks

- add stars
- he mentioned that he loads only random 20% of the data. optimize and load all
- make clouds beautiful if needed

- correct the README of gaia data

- implement LOD to start after getting basics

- increase MAX_DISTANCE_PC when downloading from gaia to get all of the stars

- ask them for the python file that converts the raw Germany team data into a csv (and place it where I process gaia data)

- modify process to process all csv files, and there was also something missing about parent

- processing so many csv files is heavy, is there a way for me from the start to download the data appropriately for the LOD? ik it has 10,000 stars limit

- their storage is with 8, so maybe its better to download data with level 8?

- his data located at Assets/data, mine is at Data. place properly

- the Level is used in download but also in the calculating player location for LOD.

- move the sun marker to standard land markers (add to csv? but he has some script)

- ask about the galactic compass (they have script)

- button 2 cycles particle size, buttons 4,5 decrease and increase the size. whats the point? ask him

- they want me to add options to teleport between landmarks. i think i will do it with zoom out - zoom in effect.

Later: move the actual star data into GPU StructuredBuffers for rendering.

Gaia provides both the observed star data (after extintion + scaterring) and the estimated real star color and properties.
We use the estimated real properties + apply our own extintion + scaterring in the simulation.
So check after everything the difference between the observed values and our results.


we have:
10,797,428 total stars
6,530,732 without 3D position
4,266,696 with 3D position
so 6.5 mil of them dont have GSP-Phot distances thus not used. fix the prob and use the data


- group the stars by clusters in order to filter for young/old clusters (a db/csv file they gave)


- add to references the method thats used to display stars from CosmoScout VR: https://elib.dlr.de/215930/1/egs20251029.pdf

- fix the photometry like in the paper for displaying stars


- Replace shared camera projection with proper per-eye XR view/projection matrices.


- fix the stars fading. when moving close or away from the stars, they should fade in and out at the rage of the angle. but they actually pop in and out when moving around

- update the readme, rn it talks about the rendering of the stars, but also add the LOD system used

- make so that the far stars cache distance would depend on the players speed. and the mslod part always has a star limit so it doesnt cares if the volume is too big

- update Clusters_README

- regenerate far stars cache when new filter is applied (as well when updating the MDLOD)

- make all of the popups cool. ask for chatgpt for a cool space design picture as a demonstration.


- change stars brightness/settings to match more realistic picture. rn its too dim so hard to enjoy the view

- remove InputActionReference menuToggleAction, InputActionReference menuSelectAction from VRMenuController.cs

- the bins load kinda buggy, when i look directly on them the whole chunk disappears, but when i look sideways it appears