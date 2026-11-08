# Theme Integration Guide

For Playnite theme authors who want PlayGif's animated descriptions to look right in their theme. It covers Desktop and Fullscreen.

There are two ways to integrate:

- **Do nothing.** PlayGif finds your theme's existing description element and puts its renderer there. Most themes work this way.
- **Place the renderer yourself.** Add PlayGif's element where you want the description, and size and style around it. See [Placing the renderer yourself](#placing-the-renderer-yourself).

This guide also covers what PlayGif handles for you and the few layouts it can't support.

---

## How PlayGif finds and places the description

1. **It uses your renderer slot if you placed one**, and otherwise looks for your description element by name: `PART_HtmlDescription`, or `DescriptionText` / `PART_Description` as fallbacks.
2. **It puts its renderer next to that element**, in the element's nearest `Panel` and in the same `Grid` cell. Your element is collapsed rather than removed, so your layout, bindings and triggers stay intact.
3. **The renderer is as tall as the whole description.** It doesn't scroll by itself. Your `ScrollViewer` scrolls it, and PlayGif clips it to that `ScrollViewer`'s visible area.

So the structure PlayGif needs is:

```xml
<ScrollViewer>                 <!-- or ScrollViewerEx; scrolls the description -->
    <Grid>                     <!-- any Panel; the renderer is added here -->
        <HtmlTextView x:Name="PART_HtmlDescription" ... />
    </Grid>
</ScrollViewer>
```

The `HtmlTextView` can also be the `ScrollViewer`'s direct content inside a `Panel` further up, as long as that `Panel` is still inside the `ScrollViewer`.

## Placing the renderer yourself

Add a `ContentControl` named `PlayGif_AnimatedDescription` where the description should go. Playnite fills it with PlayGif's renderer, the same way it fills any extension's custom element. When the slot is present, PlayGif uses it instead of searching for `PART_HtmlDescription`.

```xml
<ScrollViewer MaxHeight="850">
    <Grid MaxWidth="960" HorizontalAlignment="Left" Margin="0,0,16,0">

        <!-- Your own description: shown whenever PlayGif isn't rendering -->
        <HtmlTextView x:Name="PART_HtmlDescription" HtmlFontSize="24">
            <HtmlTextView.Style>
                <Style TargetType="HtmlTextView" BasedOn="{StaticResource {x:Type HtmlTextView}}">
                    <Style.Triggers>
                        <DataTrigger Binding="{Binding ElementName=PlayGif_AnimatedDescription, Path=Content.Visibility}"
                                     Value="Visible">
                            <Setter Property="Visibility" Value="Collapsed"/>
                        </DataTrigger>
                    </Style.Triggers>
                </Style>
            </HtmlTextView.Style>
        </HtmlTextView>

        <!-- PlayGif's renderer -->
        <ContentControl x:Name="PlayGif_AnimatedDescription"/>
    </Grid>
</ScrollViewer>
```

How the slot behaves:

- **Size and position:** set them on the `ContentControl` or its container, with `Width`/`MaxWidth`, `Margin` and alignment. The renderer fills the slot's width and is as tall as the description.
- **Scrolling:** keep a `ScrollViewer` above the slot, as with `PART_HtmlDescription`. It scrolls and clips the renderer, and keeps your theme's own scrolling, scroll bar style and controller focus.
- **Fallback:** the slot's content is `Hidden` and takes no space until the renderer is in it. It becomes `Visible` once the renderer arrives. That happens only while animated descriptions are on, and in Fullscreen only while PlayGif's Fullscreen support is on. The `DataTrigger` above keys off that, so your own description shows whenever PlayGif isn't rendering, including when it isn't installed (the binding then finds no content and the trigger never fires).
- **Where to put it:** in a game view's template, such as Desktop's details and grid views or Fullscreen's game details and main view. Playnite only fills custom elements inside view templates. Don't put it in list items: every item would get a slot of its own.
- **One slot per view:** PlayGif has a single renderer. If several slots are loaded, it uses the one in the active view.
- **Everything in [Limitations](#limitations) still applies.** The slot decides where the renderer goes, but it's still a separate window on top of your theme.

## What PlayGif handles for you

Everything in this table except the text resources needs PlayGif 1.1.0 or later. So does the renderer slot.

| Your theme does | PlayGif |
|---|---|
| Builds the description page only when it opens (on-demand pages, toggled panels, collapsed templates) | Picks it up the first time it's laid out. **You don't need to preload it** with `Visibility="Hidden"` or similar workarounds, and they can be removed. |
| Is a Fullscreen theme | Warms the renderer up in the background, so the description is ready the first time its page opens. |
| Designs for 1080p (every Fullscreen theme) | Matches Playnite's scaling to the real screen, so text size, clipping and scroll length line up at any resolution. |
| Sizes the description's container to its content (`HorizontalAlignment="Left"`, `Auto` columns) | Takes the full available width, the same as wrapping text would, up to your `MaxWidth`. |
| Slides or zooms the panel in with a `RenderTransform` animation | Keeps the description hidden until the panel stops moving, then shows it in place. |
| Defines `FontSize`, `FontFamily`, `TextColor`, `GlyphColor` resources | Uses them for the description's text and links. The `HtmlFontSize` set on the `HtmlTextView` itself is not read. |

## Limitations

The description is drawn by WebView2, which is a separate native window (HWND) placed over your theme, not a WPF element. Windows always draws it above WPF content and outside WPF's effects. Every limitation below follows from that.

- **No fades.** `Opacity`, and opacity animations on any ancestor, don't apply to the description. It appears at full opacity once the panel has settled. A short fade on the panel itself still looks fine, because the description arrives as the fade finishes.
- **Nothing can draw on top of it.** WPF elements that overlap the description's visible area are hidden underneath it, including popups, toasts, overlays and focus visuals drawn inside the main window. Playnite's own menus are separate windows and are not affected.
- **Effects don't reach it.** `Effect` (drop shadow, blur), `OpacityMask`, `Clip`, `CacheMode` and rounded corners on ancestors don't affect the description. Clipping comes only from the `ScrollViewer`.
- **Transforms are followed when the panel appears, not afterwards.** An entrance animation is handled. A `RenderTransform` that changes later while the description is visible (a zoom pulse, a parallax shift) isn't followed until the next scroll or resize.
- **Display-only.** Links and text selection don't respond. The mouse wheel and controller scroll your `ScrollViewer` directly.
- **During a fast scroll it can trail the page by about one frame.** That's the cost of keeping PlayGif off Playnite's UI thread.

## Checklist

- [ ] The description is named `PART_HtmlDescription`, or you placed a `PlayGif_AnimatedDescription` slot.
- [ ] There's a `ScrollViewer` above it, with a `Panel` between the two.
- [ ] With a slot, your own description hides when the slot's content becomes `Visible`.
- [ ] No WPF element is meant to sit on top of the description's area.
- [ ] Remove any preload workaround (such as a hidden copy of the page kept around just so PlayGif can find it).

## Troubleshooting

**Settings → PlayGif → Theme Support → Run layout report** records what PlayGif found in your theme's panel. Lines in `extensions.log` worth checking:

| Log line | Meaning |
|---|---|
| `Description element 'PART_HtmlDescription' appeared — signalling injection.` | Your on-demand page was picked up |
| `Description element 'PlayGif_RendererSlot' appeared — signalling injection.` | Your `PlayGif_AnimatedDescription` slot was picked up |
| `Description not built yet — renderer parked so it is ready when it is.` | Fullscreen warm-up is in effect |
| `Injected into Grid with HWND clipping.` | Placed correctly |
| `(no ScrollViewer, unclipped)` | There's no `ScrollViewer` above the description, so it will spill past its area |
| `Could not inject — no Panel around the description` | Wrap the description in a `Grid` |

Open an [issue](https://github.com/aHuddini/PlayGif/issues) with the report and your theme name if something still looks wrong.
