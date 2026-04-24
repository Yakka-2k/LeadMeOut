using UnityEngine;
using UnityEngine.UI;

namespace LeadMeOut
{
    public class SolidColorGraphic : Graphic
    {
        public override Material defaultMaterial
        {
            get { return Canvas.GetDefaultCanvasMaterial(); }
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            Rect r = rectTransform.rect;

            UIVertex v = new UIVertex();
            v.uv0 = Vector2.zero;

            v.color = color;
            v.position = new Vector3(r.xMin, r.yMin);
            vh.AddVert(v);

            v.position = new Vector3(r.xMin, r.yMax);
            vh.AddVert(v);

            v.position = new Vector3(r.xMax, r.yMax);
            vh.AddVert(v);

            v.position = new Vector3(r.xMax, r.yMin);
            vh.AddVert(v);

            vh.AddTriangle(0, 1, 2);
            vh.AddTriangle(2, 3, 0);
        }
    }
}