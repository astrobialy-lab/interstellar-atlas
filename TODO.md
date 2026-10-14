- correct the README of gaia data

- make build apk to account for stuff under Data as well

- move the sun marker to standard land markers (add to csv? but he has some script)

- fix the stars fading. when moving close or away from the stars, they should fade in and out at the rage of the angle. but they actually pop in and out when moving around

- update Clusters_README


- change stars brightness/settings to match more realistic picture. rn its too dim so hard to enjoy the view

- remove InputActionReference menuToggleAction, InputActionReference menuSelectAction from VRMenuController.cs


- make dust nodes visible similar to star nodes (by a press of another button)

- make so that it displays the far blocks as light blocks + top bright stars. cuz when i fly far away i dont see the real mess that there is

- update the Tools/Dust script 2_..

- all of the distance-skipped stars in Tools/Gaia step 2 can be rendered in a far away sphere sprite instead of completely discarded.
- if star is skipped by brightness, can use phot_g_mean_mag instead of mg_gspphot.
- also can use color:
                    if valid_number(ebp_rp):
                        bp_rp_intrinsic = bp_rp - ebp_rp
                    else:
                        bp_rp_intrinsic = bp_rp

- increase/descrease resolution automatically to remain stable fps (maybe change max threads that can ran in parallel when loading stars).
  based on fps or rendered/waiting nodes i can automatically change the number of threads running.
  i cant change the degree resolution cuz it will reset all of the nodes thats rendered.
  but i can but the degree resolution in settings, and manage the number of threads automatically.

- display stars using brightness lum_flame, and maybe add feature to scroll the visible band over different wavelengths.
- add feature to filter stars by: mass_flame, age_flame, evolstage_flame

- make auto-brightness: when standing close to a star or stars cluster (many light) reduce the brightness constant automatically

- add readme for dust LOD

- figure out why there is a hole in some healpixes, maybe bcz i still filter by visible brightness instead of luminosity?
  if after that hole still remains, then use that visible brightness instead

- make landmarks and sun size to decrease to minimal size

- maybe check if i can make the stars look less pixelated

- check specifically LOD of coulds in VR.

- return the dust controls, size and all
- use hist sprite + random rotations during loading
- disable sprite rotation when i rotate my head

- for stars that are close, i can see the 2x2 pixel glow effect which is ugly

- fix dust node visualizer (if there are nodes)
- remove the volumes if they are usused. check if stars extinctions is still used, if not then remove the lod generation.

- why no technion logo in vr

- fix the warnings when building apk

- remove highlighten option in the menu. no need to remember and scroll though the options. the mouse/controller picks its option. (i guess i can select if an arrow key is pressed)

- suns sprite isnt facing the VR, its facing the forward of VR (i can strafe and look left). and maybe landmarks too. same about dust sprites.

- no keyboard popup when clicking on input

- maybe indeed for menu popups its better to use world space canvas. i think i will split it? if its PC then use screen space camera/overlay, and if its VR then use world space.