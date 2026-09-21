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

Later: move the actual star data into GPU StructuredBuffers for rendering.

Gaia provides both the observed star data (after extintion + scaterring) and the estimated real star color and properties.
We use the estimated real properties + apply our own extintion + scaterring in the simulation.
So check after everything the difference between the observed values and our results.


we have:
10,797,428 total stars
6,530,732 without 3D position
4,266,696 with 3D position
so 6.5 mil of them dont have GSP-Phot distances thus not used. fix the prob and use the data