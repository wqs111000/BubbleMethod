﻿using Rhino;
using Rhino.Geometry;
using Rhino.DocObjects;
using Rhino.Collections;

using GH_IO;
using GH_IO.Serialization;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

using System;
using System.IO;
//using System.Xml;
//using System.Xml.Linq;
using System.Linq;
//using System.Data;
using System.Drawing;
using System.Reflection;
using System.Collections;
using System.Windows.Forms;
using System.Collections.Generic;
using System.Runtime.InteropServices;


using KangarooSolver;
using KangarooLib;
using KangarooSolver.Goals;
using Plankton;
using PlanktonGh;

namespace BubbleMethod
{
    public class BubbleMethodComponent : GH_Component
    {
        /// <summary>
        /// Each implementation of GH_Component must provide a public 
        /// constructor without any arguments.
        /// Category represents the Tab in which the component will appear, 
        /// Subcategory the panel. If you use non-existing tab or panel names, 
        /// new tabs/panels will automatically be created.
        /// </summary>
        public BubbleMethodComponent()
          : base("BubbleMethod", "BubbleMethod",
              "BubbleMethod",
              "ZD-MESH", "mesh")
        {
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            //0
            pManager.AddGeometryParameter("Geometry", "Geom", "Input Surface or Mesh", GH_ParamAccess.item);

            //1
            pManager.AddGenericParameter("TargetLengthFunction", "L", "A function determining local edge length", GH_ParamAccess.item);

            //2
            pManager.AddCurveParameter("FixCurves", "FixC", "Curves which will be kept sharp during remeshing. Can be boundary or internal curves", GH_ParamAccess.list);
            pManager[2].Optional = true;

            //3
            pManager.AddPointParameter("FixVertices", "FixV", "Points to keep fixed during remeshing", GH_ParamAccess.list);
            pManager[3].Optional = true;

            //4
            pManager.AddIntegerParameter("Flip", "Flip", "Criterion used to decide when to flip edges (0 for valence based, 1 for angle based)", GH_ParamAccess.item, 1);

            //5
            pManager.AddNumberParameter("PullStrength", "Pull", "Strength of pull to target geometry (between 0 and 1). Set to 0 for minimal surfaces", GH_ParamAccess.item, 0.8);

            //6
            pManager.AddIntegerParameter("Iterations", "Iter", "Number of steps between outputs", GH_ParamAccess.item, 1);

            //7
            pManager.AddBooleanParameter("Reset", "Reset", "True to initialize, false to run remeshing. Connect a timer for continuous remeshing", GH_ParamAccess.item, true);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter("Mesh", "M", "Remeshed result as Plankton Mesh", GH_ParamAccess.item);
        }

        private PlanktonMesh P = new PlanktonMesh();
        private Mesh M = new Mesh();
        private List<int> AnchorV = new List<int>();
        private List<int> FeatureV = new List<int>();
        private List<int> FeatureE = new List<int>();
        private bool initialized;
        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object can be used to retrieve data from input parameters and 
        /// to store data in output parameters.</param>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            ITargetLength TargetLength = null;
            bool reset = false;
            int Flip = 0;
            List<Curve> FC = new List<Curve>();
            List<Point3d> FV = new List<Point3d>();
            double FixT = 0.01;
            double PullStrength = 0.8;
            double SmoothStrength = 0.8;
            double LengthTol = 0.15;
            bool Minim = false;
            int Iters = 1;

            GH_ObjectWrapper Surf = new GH_ObjectWrapper();
            DA.GetData<GH_ObjectWrapper>(0, ref Surf);

            GH_ObjectWrapper Obj = null;
            DA.GetData<GH_ObjectWrapper>(1, ref Obj);
            TargetLength = Obj.Value as ITargetLength;

            DA.GetDataList<Curve>(2, FC);
            DA.GetDataList<Point3d>(3, FV);
            DA.GetData<int>(4, ref Flip);

            DA.GetData<double>(5, ref PullStrength);

            DA.GetData<int>(6, ref Iters);
            DA.GetData<bool>(7, ref reset);


            if (PullStrength == 0) { Minim = true; }
            
            if (Surf.Value is GH_Mesh)
            {
                DA.GetData<Mesh>(0, ref M);
                M.Faces.ConvertQuadsToTriangles();
            }
            else
            {
                double L = 1.0;
                MeshingParameters MeshParams = new MeshingParameters();
                MeshParams.MaximumEdgeLength = 3 * L;
                MeshParams.MinimumEdgeLength = L;
                MeshParams.JaggedSeams = false;
                MeshParams.SimplePlanes = false;
                Brep SB = null;
                DA.GetData<Brep>(0, ref SB);
                Mesh[] BrepMeshes = Mesh.CreateFromBrep(SB, MeshParams);
                M = new Mesh();
                foreach (var mesh in BrepMeshes)
                    M.Append(mesh);
            }

            if (reset || initialized == false)
            {
                #region reset
                M.Faces.ConvertQuadsToTriangles();
                P = M.ToPlanktonMesh();

                initialized = true;

                AnchorV.Clear();
                FeatureV.Clear();
                FeatureE.Clear();

                //Mark any vertices or edges lying on features
                for (int i = 0; i < P.Vertices.Count; i++)
                {
                    Point3d Pt = P.Vertices[i].ToPoint3d();
                    AnchorV.Add(-1);
                    for (int j = 0; j < FV.Count; j++)
                    {
                        if (Pt.DistanceTo(FV[j]) < FixT)
                        { AnchorV[AnchorV.Count - 1] = j; }
                    }

                    FeatureV.Add(-1);
                    for (int j = 0; j < FC.Count; j++)
                    {
                        double param = new double();
                        FC[j].ClosestPoint(Pt, out param);
                        if (Pt.DistanceTo(FC[j].PointAt(param)) < FixT)
                        { FeatureV[FeatureV.Count - 1] = j; }
                    }
                }
                //
                int EdgeCount = P.Halfedges.Count / 2;
                for (int i = 0; i < EdgeCount; i++)
                {
                    FeatureE.Add(-1);
                    //同一条线分为两条线编号分别为2i和2i+1，朝向相对，两个的起始点为
                    int vStart = P.Halfedges[2 * i].StartVertex;
                    int vEnd = P.Halfedges[2 * i + 1].StartVertex;

                    Point3d PStart = P.Vertices[vStart].ToPoint3d();
                    Point3d PEnd = P.Vertices[vEnd].ToPoint3d();

                    for (int j = 0; j < FC.Count; j++)
                    {
                        double paramS = new double();
                        double paramE = new double();
                        Curve thisFC = FC[j];
                        thisFC.ClosestPoint(PStart, out paramS);
                        thisFC.ClosestPoint(PEnd, out paramE);
                        if ((PStart.DistanceTo(thisFC.PointAt(paramS)) < FixT) &&
                          (PEnd.DistanceTo(thisFC.PointAt(paramE)) < FixT))
                        {
                            FeatureE[FeatureE.Count - 1] = j;
                        }
                    }
                }
                #endregion
            }

            else
            {
                for (int iter = 0; iter < Iters; iter++)
                {
                    int EdgeCount = P.Halfedges.Count / 2;
                    double[] EdgeLength = P.Halfedges.GetLengths();
                    List<bool> Visited = new List<bool>();
                    Vector3d[] Normals = new Vector3d[P.Vertices.Count];

                    for (int i = 0; i < P.Vertices.Count; i++)
                    {
                        Visited.Add(false);
                        Normals[i] = Normal(P, i);
                    }

                    double t = LengthTol;     //a tolerance for when to split/collapse edges
                    double smooth = SmoothStrength;  //smoothing strength
                    double pull = PullStrength;  //pull to target mesh strength               

                    // Split the edges that are too long
                    for (int i = 0; i < EdgeCount; i++)
                    {
                        if (P.Halfedges[2 * i].IsUnused == false)
                        {
                            int vStart = P.Halfedges[2 * i].StartVertex;
                            int vEnd = P.Halfedges[2 * i + 1].StartVertex;

                            if ((Visited[vStart] == false)
                              && (Visited[vEnd] == false))
                            {

                                double L2 = TargetLength.Calculate(P, 2 * i);

                                if (EdgeLength[2 * i] > (1 + t) * (4f / 3f) * L2)
                                {

                                    int SplitHEdge = P.Halfedges.TriangleSplitEdge(2 * i);
                                    if (SplitHEdge != -1)
                                    {
                                        int SplitCenter = P.Halfedges[SplitHEdge].StartVertex;
                                        P.Vertices.SetVertex(SplitCenter, MidPt(P, i));

                                        //update the feature information
                                        FeatureE.Add(FeatureE[i]);
                                        FeatureV.Add(FeatureE[i]);
                                        AnchorV.Add(-1);

                                        //2 additional new edges have also been created (or 1 if split was on a boundary)
                                        //mark these as non-features
                                        int CEdgeCount = P.Halfedges.Count / 2;
                                        while (FeatureE.Count < CEdgeCount)
                                        { FeatureE.Add(-1); }

                                        Visited.Add(true);
                                        int[] Neighbours = P.Vertices.GetVertexNeighbours(SplitCenter);
                                        foreach (int n in Neighbours)
                                        { Visited[n] = true; }
                                    }
                                }

                            }
                        }
                    }

                    //Collapse the edges that are too short
                    for (int i = 0; i < EdgeCount; i++)
                    {
                        if (P.Halfedges[2 * i].IsUnused == false)
                        {
                            int vStart = P.Halfedges[2 * i].StartVertex;
                            int vEnd = P.Halfedges[2 * i + 1].StartVertex;
                            if ((Visited[vStart] == false)
                              && (Visited[vEnd] == false))
                            {
                                if (!(AnchorV[vStart] != -1 && AnchorV[vEnd] != -1)) // if both ends are anchored, don't collapse
                                {
                                    int Collapse_option = 0; //0 for none, 1 for collapse to midpt, 2 for towards start, 3 for towards end
                                    //if neither are anchorV
                                    if (AnchorV[vStart] == -1 && AnchorV[vEnd] == -1)
                                    {
                                        // if both on same feature (or neither on a feature)
                                        if (FeatureV[vStart] == FeatureV[vEnd])
                                        { Collapse_option = 1; }
                                        // if start is on a feature and end isn't
                                        if ((FeatureV[vStart] != -1) && (FeatureV[vEnd] == -1))
                                        { Collapse_option = 2; }
                                        // if end is on a feature and start isn't
                                        if ((FeatureV[vStart] == -1) && (FeatureV[vEnd] != -1))
                                        { Collapse_option = 3; }
                                    }
                                    else // so one end must be an anchor
                                    {
                                        // if start is an anchor
                                        if (AnchorV[vStart] != -1)
                                        {
                                            // if both are on same feature, or if the end is not a feature
                                            if ((FeatureE[i] != -1) || (FeatureV[vEnd] == -1))
                                            { Collapse_option = 2; }
                                        }
                                        // if end is an anchor
                                        if (AnchorV[vEnd] != -1)
                                        {
                                            // if both are on same feature, or if the start is not a feature
                                            if ((FeatureE[i] != -1) || (FeatureV[vStart] == -1))
                                            { Collapse_option = 3; }
                                        }
                                    }

                                    Point3d Mid = MidPt(P, i);


                                    double L2 = TargetLength.Calculate(P, 2 * i);



                                    if ((Collapse_option != 0) && (EdgeLength[2 * i] < (1 - t) * 4f / 5f * L2))
                                    {
                                        int Collapsed = -1;
                                        int CollapseRtn = -1;
                                        if (Collapse_option == 1)
                                        {
                                            Collapsed = P.Halfedges[2 * i].StartVertex;
                                            P.Vertices.SetVertex(Collapsed, MidPt(P, i));
                                            CollapseRtn = P.Halfedges.CollapseEdge(2 * i);
                                        }
                                        if (Collapse_option == 2)
                                        {
                                            Collapsed = P.Halfedges[2 * i].StartVertex;
                                            CollapseRtn = P.Halfedges.CollapseEdge(2 * i);
                                        }
                                        if (Collapse_option == 3)
                                        {
                                            Collapsed = P.Halfedges[2 * i + 1].StartVertex;
                                            CollapseRtn = P.Halfedges.CollapseEdge(2 * i + 1);
                                        }
                                        if (CollapseRtn != -1)
                                        {
                                            int[] Neighbours = P.Vertices.GetVertexNeighbours(Collapsed);
                                            foreach (int n in Neighbours)
                                            { Visited[n] = true; }
                                        }
                                    }
                                }
                            }
                        }
                    }

                    EdgeCount = P.Halfedges.Count / 2;

                    if ((Flip == 0) && (PullStrength > 0))
                    {
                        //Flip edges to reduce valence error
                        for (int i = 0; i < EdgeCount; i++)
                        {
                            if (!P.Halfedges[2 * i].IsUnused
                              && (P.Halfedges[2 * i].AdjacentFace != -1)
                              && (P.Halfedges[2 * i + 1].AdjacentFace != -1)
                              && (FeatureE[i] == -1)  // don't flip feature edges
                              )
                            {
                                int Vert1 = P.Halfedges[2 * i].StartVertex;
                                int Vert2 = P.Halfedges[2 * i + 1].StartVertex;
                                int Vert3 = P.Halfedges[P.Halfedges[P.Halfedges[2 * i].NextHalfedge].NextHalfedge].StartVertex;
                                int Vert4 = P.Halfedges[P.Halfedges[P.Halfedges[2 * i + 1].NextHalfedge].NextHalfedge].StartVertex;

                                int Valence1 = P.Vertices.GetValence(Vert1);
                                int Valence2 = P.Vertices.GetValence(Vert2);
                                int Valence3 = P.Vertices.GetValence(Vert3);
                                int Valence4 = P.Vertices.GetValence(Vert4);

                                if (P.Vertices.NakedEdgeCount(Vert1) > 0) { Valence1 += 2; }
                                if (P.Vertices.NakedEdgeCount(Vert2) > 0) { Valence2 += 2; }
                                if (P.Vertices.NakedEdgeCount(Vert3) > 0) { Valence3 += 2; }
                                if (P.Vertices.NakedEdgeCount(Vert4) > 0) { Valence4 += 2; }

                                int CurrentError =
                                  Math.Abs(Valence1 - 6) +
                                  Math.Abs(Valence2 - 6) +
                                  Math.Abs(Valence3 - 6) +
                                  Math.Abs(Valence4 - 6);
                                int FlippedError =
                                  Math.Abs(Valence1 - 7) +
                                  Math.Abs(Valence2 - 7) +
                                  Math.Abs(Valence3 - 5) +
                                  Math.Abs(Valence4 - 5);
                                if (CurrentError > FlippedError)
                                {
                                    P.Halfedges.FlipEdge(2 * i);
                                }
                            }
                        }
                    }
                    else
                    {
                        //Flip edges based on angle
                        for (int i = 0; i < EdgeCount; i++)
                        {
                            if (!P.Halfedges[2 * i].IsUnused
                              && (P.Halfedges[2 * i].AdjacentFace != -1)
                              && (P.Halfedges[2 * i + 1].AdjacentFace != -1)
                              && (FeatureE[i] == -1) // don't flip feature edges
                              )
                            {
                                int Vert1 = P.Halfedges[2 * i].StartVertex;
                                int Vert2 = P.Halfedges[2 * i + 1].StartVertex;
                                int Vert3 = P.Halfedges[P.Halfedges[P.Halfedges[2 * i].NextHalfedge].NextHalfedge].StartVertex;
                                int Vert4 = P.Halfedges[P.Halfedges[P.Halfedges[2 * i + 1].NextHalfedge].NextHalfedge].StartVertex;

                                Point3d P1 = P.Vertices[Vert1].ToPoint3d();
                                Point3d P2 = P.Vertices[Vert2].ToPoint3d();
                                Point3d P3 = P.Vertices[Vert3].ToPoint3d();
                                Point3d P4 = P.Vertices[Vert4].ToPoint3d();

                                double A1 = Vector3d.VectorAngle(new Vector3d(P3 - P1), new Vector3d(P4 - P1))
                                  + Vector3d.VectorAngle(new Vector3d(P4 - P2), new Vector3d(P3 - P2));

                                double A2 = Vector3d.VectorAngle(new Vector3d(P1 - P4), new Vector3d(P2 - P4))
                                  + Vector3d.VectorAngle(new Vector3d(P2 - P3), new Vector3d(P1 - P3));

                                if (A2 > A1)
                                {
                                    P.Halfedges.FlipEdge(2 * i);
                                }
                            }
                        }
                    }

                    if (Minim)
                    {
                        Vector3d[] SmoothC = LaplacianSmooth(P, 1, smooth);

                        for (int i = 0; i < P.Vertices.Count; i++)
                        {
                            if (AnchorV[i] == -1) // don't smooth feature vertices
                            {
                                P.Vertices.MoveVertex(i, 0.5 * SmoothC[i]);
                            }
                        }
                    }

                    Vector3d[] Smooth = LaplacianSmooth(P, 0, smooth);

                    for (int i = 0; i < P.Vertices.Count; i++)
                    {
                        if (AnchorV[i] == -1) // don't smooth feature vertices
                        {
                            // make it tangential only
                            Vector3d VNormal = Normal(P, i);
                            double ProjLength = Smooth[i] * VNormal;
                            Smooth[i] = Smooth[i] - (VNormal * ProjLength);

                            P.Vertices.MoveVertex(i, Smooth[i]);

                            if (P.Vertices.NakedEdgeCount(i) != 0)//special smoothing for feature edges
                            {
                                int[] Neighbours = P.Vertices.GetVertexNeighbours(i);
                                int ncount = 0;
                                Point3d Avg = new Point3d();

                                for (int j = 0; j < Neighbours.Length; j++)
                                {
                                    if (P.Vertices.NakedEdgeCount(Neighbours[j]) != 0)
                                    {
                                        ncount++;
                                        Avg = Avg + P.Vertices[Neighbours[j]].ToPoint3d();
                                    }
                                }
                                Avg = Avg * (1.0 / ncount);
                                Vector3d move = Avg - P.Vertices[i].ToPoint3d();
                                move = move * smooth;
                                P.Vertices.MoveVertex(i, move);
                            }

                            if (FeatureV[i] != -1)//special smoothing for feature edges
                            {
                                int[] Neighbours = P.Vertices.GetVertexNeighbours(i);
                                int ncount = 0;
                                Point3d Avg = new Point3d();

                                for (int j = 0; j < Neighbours.Length; j++)
                                {
                                    if ((FeatureV[Neighbours[j]] == FeatureV[i]) || (AnchorV[Neighbours[j]] != -1))
                                    {
                                        ncount++;
                                        Avg = Avg + P.Vertices[Neighbours[j]].ToPoint3d();
                                    }
                                }
                                Avg = Avg * (1.0 / ncount);
                                Vector3d move = Avg - P.Vertices[i].ToPoint3d();
                                move = move * smooth;
                                P.Vertices.MoveVertex(i, move);
                            }

                            //projecting points onto the target along their normals

                            if (pull > 0)
                            {
                                Point3d Point = P.Vertices[i].ToPoint3d();
                                Vector3d normal = Normal(P, i);
                                Ray3d Ray1 = new Ray3d(Point, normal);
                                Ray3d Ray2 = new Ray3d(Point, -normal);
                                double RayPt1 = Rhino.Geometry.Intersect.Intersection.MeshRay(M, Ray1);
                                double RayPt2 = Rhino.Geometry.Intersect.Intersection.MeshRay(M, Ray2);
                                Point3d ProjectedPt;

                                if ((RayPt1 < RayPt2) && (RayPt1 > 0) && (RayPt1 < 1.0))
                                {
                                    ProjectedPt = Point * (1 - pull) + pull * Ray1.PointAt(RayPt1);
                                }
                                else if ((RayPt2 < RayPt1) && (RayPt2 > 0) && (RayPt2 < 1.0))
                                {
                                    ProjectedPt = Point * (1 - pull) + pull * Ray2.PointAt(RayPt2);
                                }
                                else
                                {
                                    ProjectedPt = Point * (1 - pull) + pull * M.ClosestPoint(Point);
                                }

                                P.Vertices.SetVertex(i, ProjectedPt);
                            }


                            if (FeatureV[i] != -1) //pull feature vertices onto feature curves
                            {
                                Point3d Point = P.Vertices[i].ToPoint3d();
                                Curve CF = FC[FeatureV[i]];
                                double param1 = 0.0;
                                Point3d onFeature = new Point3d();
                                CF.ClosestPoint(Point, out param1);
                                onFeature = CF.PointAt(param1);
                                P.Vertices.SetVertex(i, onFeature);
                            }
                        }
                        else
                        {
                            P.Vertices.SetVertex(i, FV[AnchorV[i]]); //pull anchor vertices onto their points
                        }
                    }




                    //end new




                    AnchorV = CompactByVertex(P, AnchorV); //compact the fixed points along with the vertices
                    FeatureV = CompactByVertex(P, FeatureV);
                    FeatureE = CompactByEdge(P, FeatureE);

                    P.Compact(); //this cleans the mesh data structure of unused elements
                }

            }

            DA.SetData(0, P);
        }


        /// <summary>
        /// Provides an Icon for every component that will be visible in the User Interface.
        /// Icons need to be 24x24 pixels.
        /// </summary>
        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                // You can add image files to your project resources and access them like this:
                //return Resources.IconForThisComponent;
                return Properties.Resources.bubble; 
            }
        }

        /// <summary>
        /// Each component must have a unique Guid to identify it. 
        /// It is vital this Guid doesn't change otherwise old ghx files 
        /// that use the old ID will partially fail during loading.
        /// </summary>
        public override Guid ComponentGuid
        {
            get { return new Guid("1c38e085-b6b3-4b07-bfd6-5f0ed49c974a"); }
        }

        private Point3d MidPt(PlanktonMesh P, int E)
        {
            Point3d Pos1 = P.Vertices[P.Halfedges[2 * E].StartVertex].ToPoint3d();
            Point3d Pos2 = P.Vertices[P.Halfedges[2 * E + 1].StartVertex].ToPoint3d();
            return (Pos1 + Pos2) * 0.5;
        }

        private List<int> CompactByVertex(PlanktonMesh P, List<int> L)
        {
            List<int> L2 = new List<int>();

            for (int i = 0; i < P.Vertices.Count; i++)
            {
                if (P.Vertices[i].IsUnused == false)
                {
                    L2.Add(L[i]);
                }
            }
            return L2;
        }

        private List<int> CompactByEdge(PlanktonMesh P, List<int> L1)
        {
            List<int> L2 = new List<int>();

            int EdgeCount = P.Halfedges.Count / 2;

            for (int i = 0; i < EdgeCount; i++)
            {
                if (P.Halfedges[2 * i].IsUnused == false)
                {
                    L2.Add(L1[i]);
                }
            }
            return L2;
        }

        private Vector3d Normal(PlanktonMesh P, int V)
        {
            Point3d Vertex = P.Vertices[V].ToPoint3d();
            Vector3d Norm = new Vector3d();

            int[] OutEdges = P.Vertices.GetHalfedges(V);
            int[] Neighbours = P.Vertices.GetVertexNeighbours(V);
            Vector3d[] OutVectors = new Vector3d[Neighbours.Length];
            int Valence = P.Vertices.GetValence(V);

            for (int j = 0; j < Valence; j++)
            {
                OutVectors[j] = P.Vertices[Neighbours[j]].ToPoint3d() - Vertex;
            }

            for (int j = 0; j < Valence; j++)
            {
                if (P.Halfedges[OutEdges[(j + 1) % Valence]].AdjacentFace != -1)
                {
                    Norm += (Vector3d.CrossProduct(OutVectors[(j + 1) % Valence], OutVectors[j]));
                }
            }

            Norm.Unitize();
            return Norm;
        }

        private double CreaseAngle(PlanktonMesh P, int HE)
        {
            if (P.Halfedges[HE].IsUnused)
            { return -1; }
            else
            {
                int Pair = P.Halfedges.GetPairHalfedge(HE);
                if (P.Halfedges[HE].AdjacentFace == -1 || P.Halfedges[Pair].AdjacentFace == -1)
                { return 0; }
                else
                {
                    int Vert1 = P.Halfedges[HE].StartVertex;
                    int Vert2 = P.Halfedges[Pair].StartVertex;
                    int Vert3 = P.Halfedges[P.Halfedges[P.Halfedges[HE].NextHalfedge].NextHalfedge].StartVertex;
                    int Vert4 = P.Halfedges[P.Halfedges[P.Halfedges[Pair].NextHalfedge].NextHalfedge].StartVertex;

                    Point3d P1 = P.Vertices[Vert1].ToPoint3d();
                    Point3d P2 = P.Vertices[Vert2].ToPoint3d();
                    Point3d P3 = P.Vertices[Vert3].ToPoint3d();
                    Point3d P4 = P.Vertices[Vert4].ToPoint3d();

                    Vector3d ThisEdge = P2 - P1;
                    Vector3d Edge1 = P3 - P1;
                    Vector3d Edge2 = P4 - P1;

                    Vector3d Normal1 = Vector3d.CrossProduct(ThisEdge, Edge1);
                    Vector3d Normal2 = Vector3d.CrossProduct(Edge2, ThisEdge);

                    return (Vector3d.VectorAngle(Normal1, Normal2));
                }
            }
        }

        public double WeightedCombo(Point3d Pos, List<Point3d> SizePoints, List<double> Sizes, int Falloff, double BVal, double BWeight)
        {
            double WeightedSize = 0, WeightSum = 0;
            double[] Weighting = new double[SizePoints.Count];

            for (int j = 0; j < SizePoints.Count; j++)
            {
                Weighting[j] = Math.Pow(Pos.DistanceTo(SizePoints[j]), -1.0 * Falloff);
                WeightSum += Weighting[j];
            }

            WeightSum += BWeight;
            WeightedSize += BWeight * (1.0 / WeightSum) * BVal;
            for (int j = 0; j < SizePoints.Count; j++)
            {
                WeightedSize += Weighting[j] * (1.0 / WeightSum) * Sizes[j];
            }
            return WeightedSize;
        }

        private static Vector3d[] LaplacianSmooth(PlanktonMesh P, int W, double Strength)
        {
            int VertCount = P.Vertices.Count;
            Vector3d[] Smooth = new Vector3d[VertCount];

            for (int i = 0; i < VertCount; i++)
            {
                if ((P.Vertices[i].IsUnused == false) && (P.Vertices.IsBoundary(i) == false))
                {
                    int[] Neighbours = P.Vertices.GetVertexNeighbours(i);
                    Point3d Vertex = P.Vertices[i].ToPoint3d();
                    Point3d Centroid = new Point3d();
                    if (W == 0)
                    {
                        for (int j = 0; j < Neighbours.Length; j++)
                        { Centroid = Centroid + P.Vertices[Neighbours[j]].ToPoint3d(); }
                        Smooth[i] = ((Centroid * (1.0 / P.Vertices.GetValence(i))) - Vertex) * Strength;
                    }
                    if (W == 1)
                    {
                        //get the radial vectors of the 1-ring
                        //get the vectors around the 1-ring
                        //get the cotangent weights for each edge

                        int valence = Neighbours.Length;

                        Point3d[] NeighbourPts = new Point3d[valence];
                        Vector3d[] Radial = new Vector3d[valence];
                        Vector3d[] Around = new Vector3d[valence];
                        double[] CotWeight = new double[valence];
                        double WeightSum = 0;

                        for (int j = 0; j < valence; j++)
                        {
                            NeighbourPts[j] = P.Vertices[Neighbours[j]].ToPoint3d();
                            Radial[j] = NeighbourPts[j] - Vertex;
                        }

                        for (int j = 0; j < valence; j++)
                        {
                            Around[j] = NeighbourPts[(j + 1) % valence] - NeighbourPts[j];
                        }

                        for (int j = 0; j < Neighbours.Length; j++)
                        {
                            //get the cotangent weights
                            int previous = (j + valence - 1) % valence;
                            Vector3d Cross1 = Vector3d.CrossProduct(Radial[previous], Around[previous]);
                            double Cross1Length = Cross1.Length;
                            double Dot1 = Radial[previous] * Around[previous];

                            int next = (j + 1) % valence;
                            Vector3d Cross2 = Vector3d.CrossProduct(Radial[next], Around[j]);
                            double Cross2Length = Cross2.Length;
                            double Dot2 = Radial[next] * Around[j];

                            CotWeight[j] = Math.Abs(Dot1 / Cross1Length) + Math.Abs(Dot2 / Cross2Length);
                            WeightSum += CotWeight[j];
                        }

                        double InvWeightSum = 1.0 / WeightSum;

                        Vector3d ThisSmooth = new Vector3d();

                        for (int j = 0; j < Neighbours.Length; j++)
                        {
                            ThisSmooth = ThisSmooth + Radial[j] * CotWeight[j];
                        }

                        Smooth[i] = ThisSmooth * InvWeightSum * Strength;
                    }

                }
            }
            return Smooth;
        }
    }
}
