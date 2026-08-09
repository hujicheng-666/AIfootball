using System;
using System.Windows;
using System.Windows.Media;

namespace AIfootball.App;

public sealed class PenaltyKeeperControl : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(PenaltyKeeperControl),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double p = Math.Clamp(Progress, 0d, 1d);
        Pose pose = GetPose(p);
        Point hip = pose.Hip;
        Point shoulder = pose.Shoulder;
        Point head = pose.Head;
        Point leftHand = pose.LeftHand;
        Point rightHand = pose.RightHand;
        Point leftFoot = pose.LeftFoot;
        Point rightFoot = pose.RightFoot;
        Point leftElbow = pose.LeftElbow;
        Point rightElbow = pose.RightElbow;
        Point leftKnee = pose.LeftKnee;
        Point rightKnee = pose.RightKnee;
        Vector torsoAxis = hip - shoulder;
        double torsoLength = Math.Max(torsoAxis.Length, 1d);
        Vector torsoNormal = new(-torsoAxis.Y / torsoLength, torsoAxis.X / torsoLength);
        Point shoulderLeft = Add(shoulder, torsoNormal, -24d);
        Point shoulderRight = Add(shoulder, torsoNormal, 25d);
        Point hipRight = Add(hip, torsoNormal, 30d);
        Point hipLeft = Add(hip, torsoNormal, -30d);

        var shadow = new SolidColorBrush(Color.FromArgb(80, 19, 211, 229));
        var glowPen = new Pen(new SolidColorBrush(Color.FromArgb(160, 103, 236, 248)), 8)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        var limbPen = new Pen(new SolidColorBrush(Color.FromRgb(119, 220, 233)), 12)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        var outline = new Pen(new SolidColorBrush(Color.FromRgb(205, 250, 255)), 1.5);

        dc.DrawLine(glowPen, Offset(shoulder, 6, 7), Offset(leftHand, 6, 7));
        dc.DrawLine(glowPen, Offset(shoulder, 6, 7), Offset(rightHand, 6, 7));
        dc.DrawLine(glowPen, Offset(hip, 6, 7), Offset(leftFoot, 6, 7));
        dc.DrawLine(glowPen, Offset(hip, 6, 7), Offset(rightFoot, 6, 7));

        DrawLimb(dc, shoulder, leftElbow, leftHand, limbPen, outline);
        DrawLimb(dc, shoulder, rightElbow, rightHand, limbPen, outline);
        DrawLimb(dc, hip, leftKnee, leftFoot, limbPen, outline);
        DrawLimb(dc, hip, rightKnee, rightFoot, limbPen, outline);

        var torso = new StreamGeometry();
        using (var context = torso.Open())
        {
            context.BeginFigure(shoulderLeft, true, true);
            context.LineTo(shoulderRight, true, false);
            context.LineTo(hipRight, true, false);
            context.LineTo(hipLeft, true, false);
        }
        torso.Freeze();
        dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(30, 137, 151)), outline, torso);

        var torsoFacet = new StreamGeometry();
        using (var context = torsoFacet.Open())
        {
            context.BeginFigure(shoulder, true, true);
            context.LineTo(shoulderRight, true, false);
            context.LineTo(hipRight, true, false);
            context.LineTo(hip, true, false);
        }
        torsoFacet.Freeze();
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(185, 86, 213, 224)), null, torsoFacet);

        dc.DrawEllipse(shadow, null, Offset(head, 5, 7), 25, 25);
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(10, 38, 49)), outline, head, 23, 25);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(100, 157, 240, 248)), null, Offset(head, -6, -6), 9, 10);

        DrawJoint(dc, shoulder);
        DrawJoint(dc, hip);
        DrawJoint(dc, leftElbow);
        DrawJoint(dc, rightElbow);
        DrawJoint(dc, leftKnee);
        DrawJoint(dc, rightKnee);
    }

    private static void DrawLimb(DrawingContext dc, Point start, Point joint, Point end, Pen limbPen, Pen outline)
    {
        dc.DrawLine(limbPen, start, joint);
        dc.DrawLine(limbPen, joint, end);
        dc.DrawLine(outline, start, joint);
        dc.DrawLine(outline, joint, end);
    }

    private static void DrawJoint(DrawingContext dc, Point center)
    {
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(219, 253, 255)), null, center, 4, 4);
    }

    private static Point Lerp(Point from, Point to, double progress) =>
        new(from.X + (to.X - from.X) * progress, from.Y + (to.Y - from.Y) * progress);

    private static Point Offset(Point point, double x, double y) => new(point.X + x, point.Y + y);

    private static Point Add(Point point, Vector vector, double factor) =>
        new(point.X + vector.X * factor, point.Y + vector.Y * factor);

    private static Pose GetPose(double progress)
    {
        Pose stand = new(
            new Point(94, 142), new Point(94, 83), new Point(94, 50),
            new Point(46, 112), new Point(142, 112), new Point(56, 205), new Point(132, 205),
            new Point(58, 98), new Point(132, 98), new Point(70, 168), new Point(120, 168));
        Pose load = new(
            new Point(98, 151), new Point(112, 106), new Point(118, 76),
            new Point(160, 101), new Point(180, 91), new Point(53, 207), new Point(142, 204),
            new Point(142, 104), new Point(157, 93), new Point(72, 177), new Point(126, 180));
        Pose launch = new(
            new Point(137, 130), new Point(170, 91), new Point(194, 67),
            new Point(265, 40), new Point(253, 69), new Point(94, 187), new Point(170, 170),
            new Point(225, 54), new Point(220, 76), new Point(118, 158), new Point(162, 149));
        Pose dive = new(
            new Point(176, 112), new Point(226, 80), new Point(253, 58),
            new Point(306, 39), new Point(295, 67), new Point(96, 157), new Point(165, 164),
            new Point(274, 48), new Point(267, 70), new Point(133, 137), new Point(198, 140));
        Pose landing = new(
            new Point(183, 132), new Point(224, 104), new Point(246, 84),
            new Point(292, 69), new Point(280, 101), new Point(128, 185), new Point(207, 178),
            new Point(264, 80), new Point(255, 106), new Point(158, 159), new Point(203, 155));

        if (progress < 0.16d) return Interpolate(stand, load, progress / 0.16d);
        if (progress < 0.42d) return Interpolate(load, launch, (progress - 0.16d) / 0.26d);
        if (progress < 0.68d) return Interpolate(launch, dive, (progress - 0.42d) / 0.26d);
        if (progress < 0.9d) return dive;
        if (progress < 0.96d) return Interpolate(dive, landing, (progress - 0.9d) / 0.06d);
        return Interpolate(landing, stand, (progress - 0.96d) / 0.04d);
    }

    private static Pose Interpolate(Pose from, Pose to, double progress)
    {
        double eased = progress * progress * (3d - 2d * progress);
        return new Pose(
            Lerp(from.Hip, to.Hip, eased), Lerp(from.Shoulder, to.Shoulder, eased), Lerp(from.Head, to.Head, eased),
            Lerp(from.LeftHand, to.LeftHand, eased), Lerp(from.RightHand, to.RightHand, eased),
            Lerp(from.LeftFoot, to.LeftFoot, eased), Lerp(from.RightFoot, to.RightFoot, eased),
            Lerp(from.LeftElbow, to.LeftElbow, eased), Lerp(from.RightElbow, to.RightElbow, eased),
            Lerp(from.LeftKnee, to.LeftKnee, eased), Lerp(from.RightKnee, to.RightKnee, eased));
    }

    private readonly record struct Pose(
        Point Hip, Point Shoulder, Point Head, Point LeftHand, Point RightHand, Point LeftFoot, Point RightFoot,
        Point LeftElbow, Point RightElbow, Point LeftKnee, Point RightKnee);
}
