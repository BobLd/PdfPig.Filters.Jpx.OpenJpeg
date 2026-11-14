namespace OpenJpeg.Internal;

internal static class Invert
{
    /// <summary>
    /// Matrix inversion.
    /// </summary>
    public static bool MatrixInversion(float[] SrcMatrix,
                                       float[] DestMatrix,
                                       int n_compo)
    {
        int permutation_size = n_compo;
        int swap_size = n_compo;

        int[] permutations = new int[permutation_size];
        float[] double_data = new float[swap_size];
        float[] dest_temp = new float[swap_size];
        float[] swap_area = new float[swap_size];

        if (!lupDecompose(SrcMatrix, permutations, double_data, n_compo))
            return false;

        lupInvert(SrcMatrix, DestMatrix, n_compo, permutations, double_data, dest_temp, swap_area);

        return true;
    }

    private static bool lupDecompose(float[] matrix, int[] permutations,
                                     float[] swap_area, int n_compo)
    {
        int tmpPermutations = 0;
        int k2 = 0;
        int lLastColum = n_compo - 1;
        int lTmpMatrix = 0;
        int offset = 1;
        int lStride = n_compo - 1;

        /*initialize permutations */
        for (int i = 0; i < n_compo; ++i)
        {
            permutations[tmpPermutations++] = i;
        }

        /* now make a pivot with colum switch */
        tmpPermutations = 0;
        for (int k = 0; k < lLastColum; ++k)
        {
            float p = 0f;

            /* take the middle element */
            int lColumnMatrix = lTmpMatrix + k;

            /* make permutation with the biggest value in the column */
            float temp;
            for (int i = k; i < n_compo; ++i)
            {
                temp = ((matrix[lColumnMatrix] > 0) ? matrix[lColumnMatrix] : -(matrix[lColumnMatrix]));
                if (temp > p)
                {
                    p = temp;
                    k2 = i;
                }
                /* next line */
                lColumnMatrix += n_compo;
            }

            /* a whole rest of 0 -> non singular */
            if (p == 0.0)
                return false;

            /* should we permute ? */
            if (k2 != k)
            {
                /*exchange of line */
                /* k2 > k */
                int dstPermutations = tmpPermutations + k2 - k;
                /* swap indices */
                (permutations[tmpPermutations], permutations[dstPermutations]) = (permutations[dstPermutations], permutations[tmpPermutations]);

                /* and swap entire line. */
                lColumnMatrix = lTmpMatrix + (k2 - k) * n_compo;
                Array.Copy(matrix, lColumnMatrix, swap_area, 0, n_compo);
                Array.Copy(matrix, lTmpMatrix, matrix, lColumnMatrix, n_compo);
                Array.Copy(swap_area, 0, matrix, lTmpMatrix, n_compo);
            }

            /* now update data in the rest of the line and line after */
            int lDestMatrix = lTmpMatrix + k;
            lColumnMatrix = lDestMatrix + n_compo;
            /* take the middle element */
            temp = matrix[lDestMatrix++];

            /* now compute up data (i.e. coeff up of the diagonal). */
            for (int i = offset; i < n_compo; ++i)
            {
                /*lColumnMatrix; */
                /* divide the lower column elements by the diagonal value */

                /* matrix[i][k] /= matrix[k][k]; */
                /* p = matrix[i][k] */
                p = matrix[lColumnMatrix] / temp;
                matrix[lColumnMatrix++] = p;

                for (int j = /* k + 1 */ offset; j < n_compo; ++j)
                {
                    /* matrix[i][j] -= matrix[i][k] * matrix[k][j]; */
                    matrix[lColumnMatrix++] -= p * matrix[lDestMatrix++];
                }
                /* come back to the k+1th element */
                lDestMatrix -= lStride;
                /* go to kth element of the next line */
                lColumnMatrix += k;
            }

            /* offset is now k+2 */
            ++offset;
            /* 1 element less for stride */
            --lStride;
            /* next line */
            lTmpMatrix += n_compo;
            /* next permutation element */
            ++tmpPermutations;
        }
        return true;
    }

    private static void lupSolve(float[] result,
                                 float[] matrix,
                                 float[] vector,
                                 int[] permutations,
                                 int n_compo, float[] intermediate_data)
    {
        int lStride = n_compo + 1;
        int lLineMatrix = 0;
        int lBeginPtr = n_compo - 1;
        int lCurrentPermutationPtr = 0;

        int lIntermediatePtr = 0;
        int lGeneratedData = n_compo - 1;

        for (int i = 0; i < n_compo; ++i)
        {
            float sum = 0f;
            int lCurrentPtr = 0;
            int lTmpMatrix = lLineMatrix;
            for (int j = 1; j <= i; ++j)
            {
                /* sum += matrix[i][j-1] * y[j-1]; */
                sum += matrix[lTmpMatrix++] * intermediate_data[lCurrentPtr++];
            }
            /*y[i] = pVector[pPermutations[i]] - sum; */
            intermediate_data[lIntermediatePtr++] = vector[permutations[lCurrentPermutationPtr++]] - sum;
            lLineMatrix += n_compo;
        }

        /* we take the last point of the matrix */
        lLineMatrix = n_compo * n_compo - 1;

        /* and we take after the last point of the destination vector */
        int lDestPtr = n_compo;

        System.Diagnostics.Debug.Assert(n_compo != 0);
        for (int k = n_compo - 1; k != -1; --k)
        {
            float sum = 0f;
            int lTmpMatrix = lLineMatrix;
            float u = matrix[lTmpMatrix++];
            int lCurrentPtr = lDestPtr--;
            for (int j = (k + 1); j < n_compo; ++j)
            {
                /* sum += matrix[k][j] * x[j] */
                sum += matrix[lTmpMatrix++] * intermediate_data[lCurrentPtr++];
            }
            /*x[k] = (y[k] - sum) / u; */
            result[lBeginPtr--] = (intermediate_data[lGeneratedData--] - sum) / u;
            lLineMatrix -= lStride;
        }
    }

    private static void lupInvert(float[] SrcMatrix,
                                  float[] DestMatrix,
                                  int n_compo,
                                  int[] permutations,
                                  float[] src_temp,
                                  float[] dest_temp,
                                  float[] swap_area)
    {
        int lLineMatrix = 0;

        for (int j = 0; j < n_compo; ++j)
        {
            int lCurrentPtr = lLineMatrix++;
            for (int c = 0; c < src_temp.Length; c++)
            {
                src_temp[c] = 0;
            }
            src_temp[j] = 1f;
            lupSolve(dest_temp, SrcMatrix, src_temp, permutations, n_compo, swap_area);

            for (int i = 0; i < n_compo; ++i)
            {
                DestMatrix[lCurrentPtr] = dest_temp[i];
                lCurrentPtr += n_compo;
            }
        }
    }
}
