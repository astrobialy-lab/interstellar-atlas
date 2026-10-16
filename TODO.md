- correct the README of gaia data

- update Clusters_README

- figure out why there is a hole in some healpixes, maybe bcz i still filter by visible brightness instead of luminosity?
  if after that hole still remains, then use that visible brightness instead
- if star is skipped by brightness, can use phot_g_mean_mag instead of mg_gspphot.
- also can use color:
                    if valid_number(ebp_rp):
                        bp_rp_intrinsic = bp_rp - ebp_rp
                    else:
                        bp_rp_intrinsic = bp_rp

- display stars using brightness lum_flame, and maybe add feature to scroll the visible band over different wavelengths.
- add feature to filter stars by: mass_flame, age_flame, evolstage_flame, teff_gspphot

- fix the warnings when building apk

- check if all controls in VR work

BONUS:
- maybe increase landmarks resolution

- maybe check if i can make the stars look less pixelated
- for stars that are close, i can see the 2x2 pixel glow effect which is ugly

- increase/descrease "max current loads" (in stars) automatically to remain stable fps (maybe change max threads that can run in parallel when loading stars).
  based on fps or rendered/waiting nodes i can automatically change the number of threads running.
  i cant change the degree resolution cuz it will reset all of the nodes thats rendered.
  but i can but the degree resolution in settings, and manage the number of threads automatically.
- or add threads to the settings menu