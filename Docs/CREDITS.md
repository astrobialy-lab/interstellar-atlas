### Milky Way Background

- **Title:** The Milky Way Panorama
- **Credit:** ESO/S. Brunier
- **Source:** https://www.eso.org/public/images/eso0932a/
- **License:** Creative Commons Attribution 4.0 International (CC BY 4.0)
- **License URL:** https://creativecommons.org/licenses/by/4.0/

### Gaia DR3 Star Catalogue

- **Credit:** European Space Agency (ESA), Gaia mission, and Gaia Data Processing and Analysis Consortium (DPAC)
- **Source:** https://www.cosmos.esa.int/gaia
- **Dataset:** Gaia Data Release 3
- **License:** CC BY-NC 3.0 IGO
- **License URL:** https://creativecommons.org/licenses/by-nc/3.0/igo/
- **References:** Gaia Collaboration et al. (2016), Astronomy & Astrophysics, 595, A1; Gaia Collaboration et al. (2023), Astronomy & Astrophysics, 674, A1.

The Gaia data license prohibits commercial use without separate authorization.

### Open Cluster Catalogue

- **Authors:** Emily L. Hunt and Sabine Reffert (2024)
- **Title:** Improving the open cluster census. III. Using cluster masses, radii, and dynamics to create a cleaned open cluster catalogue.
- **Publication:** Astronomy & Astrophysics, 686, A42.
- **Article DOI:** https://doi.org/10.1051/0004-6361/202348662
- **Catalogue:** CDS/VizieR J/A+A/686/A42
- **Catalogue DOI:** https://doi.org/10.26093/cds/vizier.36860042
- **Article license:** CC BY 4.0. The separate licensing status of the catalogue files should be confirmed with CDS or the authors before commercial redistribution.

### Three-Dimensional Dust Map

- **Authors:** Edenhofer, G. et al. (2024).
- **Title:** A parsec-scale Galactic 3D dust map out to 1.25 kpc from the Sun.
- **Publication:** Astronomy & Astrophysics, 685, A82.
- **Article DOI:** https://doi.org/10.1051/0004-6361/202347628
- **Dataset DOI:** https://doi.org/10.5281/zenodo.8187942
- **Dataset license:** Creative Commons Attribution 4.0 International (CC BY 4.0).
- **License URL:** https://creativecommons.org/licenses/by/4.0/

### Astronomical Landmarks

The astronomical landmarks in Interstellar Atlas are compiled from several astronomical databases, published catalogues, and research papers. Selected object positions, classifications, and distance measurements are used to create representative landmarks in the application.

#### SIMBAD Astronomical Database

- **Maintained by:** Centre de Données astronomiques de Strasbourg (CDS).
- **Source:** [SIMBAD Astronomical Database](https://simbad.cds.unistra.fr/simbad/)
- **Reference:** Wenger et al. (2000), *The SIMBAD astronomical database. The CDS reference database for astronomical objects.*
- **Publication:** Astronomy and Astrophysics Supplement Series, 143, 9.
- **DOI:** https://doi.org/10.1051/aas:2000332
- **License:** Open Data Commons Open Database License (ODbL).
- **License URL:** https://opendatacommons.org/licenses/odbl/1-0/

SIMBAD is used for astronomical object identification, coordinates, classifications, spectral types, and available distance or parallax measurements.

Acknowledgement: This application has made use of the SIMBAD database, operated at CDS, Strasbourg, France.

#### Open Star Cluster Landmarks

- **Authors:** Emily L. Hunt and Sabine Reffert (2024).
- **Title:** *Improving the open cluster census III. Using cluster masses, radii, and dynamics to create a cleaned open cluster catalogue.*
- **Publication:** Astronomy & Astrophysics, 686, A42.
- **Article DOI:** https://doi.org/10.1051/0004-6361/202348662
- **VizieR Catalogue:** [J/A+A/686/A42](https://cdsarc.cds.unistra.fr/viz-bin/cat/J/A%2BA/686/A42)
- **VizieR Catalogue DOI:** https://doi.org/10.26093/cds/vizier.36860042
- **Article License:** Creative Commons Attribution 4.0 International (CC BY 4.0).
- **License URL:** https://creativecommons.org/licenses/by/4.0/

The published catalogue is used to identify open clusters and obtain their coordinates and distance estimates.

Acknowledgement: This application has made use of the VizieR catalogue access tool, CDS, Strasbourg, France.

#### Molecular Cloud Landmarks — Zucker et al. (2021)

- **Authors:** Catherine Zucker et al. (2021).
- **Title:** *On the Three-Dimensional Structure of Local Molecular Clouds.*
- **Publication:** The Astrophysical Journal, 919, 35.
- **Article DOI:** https://doi.org/10.3847/1538-4357/ac1f96
- **arXiv:** https://arxiv.org/abs/2109.09765
- **Data Used:** Table 1, "Skeletonization Results."
- **License:** Creative Commons Attribution 4.0 International (CC BY 4.0), arXiv version.
- **License URL:** https://creativecommons.org/licenses/by/4.0/

Selected molecular cloud measurements are used to calculate representative landmark positions. The published median Galactic longitude, latitude, and heliocentric distance are converted into the application's Sun-centred Galactic Cartesian coordinate system.

#### Molecular Cloud Distances — Schlafly et al. (2014)

- **Authors:** E. F. Schlafly et al. (2014).
- **Title:** *A Large Catalog of Accurate Distances to Molecular Clouds from PS1 Photometry.*
- **Publication:** The Astrophysical Journal, 786, 29.
- **Article DOI:** https://doi.org/10.1088/0004-637X/786/1/29
- **arXiv:** https://arxiv.org/abs/1403.3393
- **Catalogue Website:** https://e.schlaf.ly/distances/
- **Binary Catalogue:** https://e.schlaf.ly/distances/fitscatalog.html

Selected cloud sightlines, Galactic coordinates, and distance estimates from the major-cloud and MBM catalogues are used to calculate representative molecular cloud landmark positions.

The catalogue website does not specify a standalone reuse license on its landing page; the applicable data reuse terms should be checked separately.

#### Descriptive Text

- **Source:** [Wikipedia](https://www.wikipedia.org/)
- **API:** [MediaWiki Action API](https://www.mediawiki.org/wiki/API:Main_page)
- **License:** Wikipedia article text is generally available under Creative Commons Attribution-ShareAlike 4.0 International (CC BY-SA 4.0).
- **License URL:** https://creativecommons.org/licenses/by-sa/4.0/

Short excerpts from relevant Wikipedia articles are used to provide descriptive context for selected landmarks. The excerpts are shortened and incorporated into the application's descriptions.

#### Coordinate Processing and Visualization

Catalogue positions and distance measurements are adapted for visualization in Interstellar Atlas.

- Coordinates are converted to heliocentric Galactic Cartesian coordinates.
- The application uses the mapping Unity X = Galactic X, Unity Y = Galactic Z, and Unity Z = Galactic Y.
- Extended objects are represented by individual approximate position markers; these markers do not reproduce their complete physical extent or three-dimensional structure.
- The Sun, Galactic Center, and Local Bubble use explicitly defined application reference or visualization anchors rather than ordinary catalogue-derived object positions.