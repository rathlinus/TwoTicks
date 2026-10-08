package main

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"image"
	"image/color"
	"image/jpeg"
	_ "image/png"
	"io"
	"math"
	"net/http"

	"golang.org/x/image/draw"
)

const (
	// OpenStreetMap asks tile users to name their app instead of posing as a browser.
	mapUserAgent = "TwoTicks (location previews)"
	mapTileSize  = 256
	// Tiles at this zoom are drawn at half size, so the picture stays sharp
	// on high-DPI screens and shows a few streets around the place.
	mapZoom   = 17
	mapWidth  = 480
	mapHeight = 320
	maxTile   = 1 << 20
)

// mapPreview draws the place a location message points at from OpenStreetMap
// tiles, with a pin in the middle, for messages that came without a picture.
func mapPreview(ctx context.Context, lat, lng float64) ([]byte, error) {
	if lat < -85 || lat > 85 || lng < -180 || lng > 180 {
		return nil, errors.New("location is off the map")
	}
	n := float64(int(1) << mapZoom)
	// The place in pixels on the whole map at this zoom.
	px := (lng + 180) / 360 * n * mapTileSize
	sin := math.Sin(lat * math.Pi / 180)
	py := (0.5 - math.Log((1+sin)/(1-sin))/(4*math.Pi)) * n * mapTileSize

	// The full-size area around the place that becomes the picture.
	left := int(math.Round(px)) - mapWidth
	top := int(math.Round(py)) - mapHeight
	area := image.NewRGBA(image.Rect(0, 0, mapWidth*2, mapHeight*2))
	draw.Draw(area, area.Bounds(), image.NewUniform(color.RGBA{0xe8, 0xe4, 0xdc, 0xff}), image.Point{}, draw.Src)

	tiles := int(n)
	for ty := floorDiv(top, mapTileSize); ty*mapTileSize < top+mapHeight*2; ty++ {
		for tx := floorDiv(left, mapTileSize); tx*mapTileSize < left+mapWidth*2; tx++ {
			if ty < 0 || ty >= tiles {
				continue
			}
			tile, err := mapTile(ctx, ((tx%tiles)+tiles)%tiles, ty)
			if err != nil {
				return nil, err
			}
			at := image.Pt(tx*mapTileSize-left, ty*mapTileSize-top)
			draw.Draw(area, image.Rectangle{Min: at, Max: at.Add(image.Pt(mapTileSize, mapTileSize))}, tile, tile.Bounds().Min, draw.Src)
		}
	}

	picture := image.NewRGBA(image.Rect(0, 0, mapWidth, mapHeight))
	draw.CatmullRom.Scale(picture, picture.Bounds(), area, area.Bounds(), draw.Src, nil)
	drawPin(picture, mapWidth/2, mapHeight/2)

	var buf bytes.Buffer
	if err := jpeg.Encode(&buf, picture, &jpeg.Options{Quality: 80}); err != nil {
		return nil, err
	}
	return buf.Bytes(), nil
}

func mapTile(ctx context.Context, x, y int) (image.Image, error) {
	address := fmt.Sprintf("https://tile.openstreetmap.org/%d/%d/%d.png", mapZoom, x, y)
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, address, nil)
	if err != nil {
		return nil, err
	}
	req.Header.Set("User-Agent", mapUserAgent)
	resp, err := previewClient.Do(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("map tile: %s", resp.Status)
	}
	tile, _, err := image.Decode(io.LimitReader(resp.Body, maxTile))
	return tile, err
}

// drawPin marks the place with a red dot in a white ring and a soft shadow.
func drawPin(img *image.RGBA, cx, cy int) {
	disc(img, cx, cy+2, 13, color.RGBA{0, 0, 0, 0x40})
	disc(img, cx, cy, 12, color.RGBA{0xff, 0xff, 0xff, 0xff})
	disc(img, cx, cy, 9, color.RGBA{0xe5, 0x39, 0x35, 0xff})
}

func disc(img *image.RGBA, cx, cy int, r float64, c color.RGBA) {
	reach := int(math.Ceil(r)) + 1
	for y := cy - reach; y <= cy+reach; y++ {
		for x := cx - reach; x <= cx+reach; x++ {
			if !(image.Point{x, y}).In(img.Bounds()) {
				continue
			}
			// Soften the edge over one pixel so the circle is not jagged.
			cover := math.Min(1, math.Max(0, r+0.5-math.Hypot(float64(x-cx), float64(y-cy))))
			if cover == 0 {
				continue
			}
			alpha := cover * float64(c.A) / 255
			under := img.RGBAAt(x, y)
			img.SetRGBA(x, y, color.RGBA{
				R: uint8(float64(c.R)*alpha + float64(under.R)*(1-alpha)),
				G: uint8(float64(c.G)*alpha + float64(under.G)*(1-alpha)),
				B: uint8(float64(c.B)*alpha + float64(under.B)*(1-alpha)),
				A: 0xff,
			})
		}
	}
}

func floorDiv(a, b int) int {
	q := a / b
	if a%b != 0 && (a < 0) != (b < 0) {
		q--
	}
	return q
}
