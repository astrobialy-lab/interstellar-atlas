- correct the README of gaia data

- update Clusters_README

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

- figure out why there is a hole in some healpixes, maybe bcz i still filter by visible brightness instead of luminosity?
  if after that hole still remains, then use that visible brightness instead

- fix the warnings when building apk

- no keyboard popup when clicking on input. maybe its bcz the text before it has too wide div?
- maybe it shows if i could click on stuff properly with x instead of grabbing

- check if all controls in VR work


- disable controls when displaying landmark info



BONUS:
- maybe increase landmarks resolution

- maybe check if i can make the stars look less pixelated
- for stars that are close, i can see the 2x2 pixel glow effect which is ugly

