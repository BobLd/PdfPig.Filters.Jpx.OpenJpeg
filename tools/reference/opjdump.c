/* Usage: opjdump in out [reduce] [area]
   Dumps decoded component samples (after lib-level pclr/cdef) as: "OJD1", ncomps, colorspace,
   then per comp: w h dx dy x0 y0 prec sgnd (int32 LE), then w*h int32 samples.
   Exit 1 on decode failure, 3 if the dump would exceed 1 GB (not written, to protect the disk). */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "openjpeg.h"
static void quiet(const char *m, void *d) { (void)m; (void)d; }
int main(int argc, char **argv) {
    if (argc < 3) { fprintf(stderr, "usage: opjdump in out [reduce]\n"); return 2; }
    FILE *f = fopen(argv[1], "rb"); unsigned char hdr[12] = {0};
    if (!f || fread(hdr, 1, 12, f) < 4) return 1; fclose(f);
    OPJ_CODEC_FORMAT fmt = (hdr[0] == 0xFF && hdr[1] == 0x4F) ? OPJ_CODEC_J2K : OPJ_CODEC_JP2;
    opj_codec_t *c = opj_create_decompress(fmt);
    opj_set_error_handler(c, quiet, NULL); opj_set_warning_handler(c, quiet, NULL); opj_set_info_handler(c, quiet, NULL);
    opj_dparameters_t p; opj_set_default_decoder_parameters(&p);
    if (argc > 3) p.cp_reduce = atoi(argv[3]);
    int area = argc > 4 && strcmp(argv[4], "area") == 0; /* decode the centre quarter: exercises partial decoding */
    opj_stream_t *s = opj_stream_create_default_file_stream(argv[1], OPJ_TRUE);
    opj_image_t *img = NULL;
    if (!opj_setup_decoder(c, &p) || !opj_read_header(s, c, &img)) return 1;
    if (area) {
        OPJ_UINT32 w = img->x1 - img->x0, h = img->y1 - img->y0;
        if (!opj_set_decode_area(c, img, (OPJ_INT32)(img->x0 + w / 4), (OPJ_INT32)(img->y0 + h / 4),
                                 (OPJ_INT32)(img->x0 + w / 4 + w / 2), (OPJ_INT32)(img->y0 + h / 4 + h / 2))) return 1;
    }
    if (!opj_decode(c, s, img) || !opj_end_decompress(c, s)) return 1;
    unsigned long long total = 0;
    for (OPJ_UINT32 i = 0; i < img->numcomps; i++) total += (unsigned long long)img->comps[i].w * img->comps[i].h;
    if (total * 4 > (1ULL << 30)) return 3;
    FILE *o = fopen(argv[2], "wb"); int v;
    fwrite("OJD1", 1, 4, o); v = img->numcomps; fwrite(&v, 4, 1, o); v = img->color_space; fwrite(&v, 4, 1, o);
    for (OPJ_UINT32 i = 0; i < img->numcomps; i++) {
        opj_image_comp_t *k = &img->comps[i];
        int h[8] = { (int)k->w, (int)k->h, (int)k->dx, (int)k->dy, (int)k->x0, (int)k->y0, (int)k->prec, (int)k->sgnd };
        fwrite(h, 4, 8, o);
        if (k->data) fwrite(k->data, 4, (size_t)k->w * k->h, o);
    }
    fclose(o); return 0;
}
