package main

import (
	"bytes"
	"context"
	"errors"
	"image"
	"io"
	"mime"
	"net/http"
	"net/url"
	"strings"
	"time"

	"golang.org/x/net/html"
	"golang.org/x/net/html/charset"
)

const (
	// Sites shape their previews for WhatsApp's own fetcher, which it names
	// itself like this; some give no preview to anything else.
	previewUserAgent = "WhatsApp/2.2437.2 W"
	maxPreviewPage   = 1 << 20
	maxPreviewImage  = 8 << 20
	previewThumbSize = 256
)

var previewClient = &http.Client{
	Timeout: 10 * time.Second,
	CheckRedirect: func(req *http.Request, via []*http.Request) error {
		if len(via) >= 5 {
			return errors.New("too many redirects")
		}
		return nil
	},
}

// linkPreview reads the title, description and picture a web page gives for
// sharing, as WhatsApp's apps do before they send a link. It returns nil when
// the page has none. rawURL is the link as it is in the message; one without
// a scheme, such as www.example.com, is fetched over https.
func linkPreview(ctx context.Context, rawURL string) (*Link, error) {
	address := rawURL
	if !strings.Contains(address, "://") {
		address = "https://" + address
	}
	u, err := url.Parse(address)
	if err != nil || (u.Scheme != "http" && u.Scheme != "https") || u.Host == "" {
		return nil, errors.New("not a web address")
	}

	resp, err := previewGet(ctx, u.String(), "text/html,application/xhtml+xml")
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	mediaType, _, _ := mime.ParseMediaType(resp.Header.Get("Content-Type"))
	if resp.StatusCode != http.StatusOK || (mediaType != "text/html" && mediaType != "application/xhtml+xml") {
		return nil, nil
	}
	body, err := charset.NewReader(io.LimitReader(resp.Body, maxPreviewPage), resp.Header.Get("Content-Type"))
	if err != nil {
		return nil, nil
	}
	meta := pageMeta(body)

	title := firstOf(meta["og:title"], meta["twitter:title"], meta["title"])
	description := firstOf(meta["og:description"], meta["twitter:description"], meta["description"])
	if title == "" && description == "" {
		return nil, nil
	}
	link := &Link{URL: rawURL, Title: truncate(title, 200), Description: truncate(description, 300)}
	if picture := firstOf(meta["og:image:secure_url"], meta["og:image"], meta["og:image:url"], meta["twitter:image"]); picture != "" {
		// The address of the picture is relative to the page it came from, after redirects.
		if ref, err := resp.Request.URL.Parse(picture); err == nil && (ref.Scheme == "http" || ref.Scheme == "https") {
			link.Thumb = previewThumbnail(ctx, ref.String())
		}
	}
	return link, nil
}

func previewGet(ctx context.Context, address, accept string) (*http.Response, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, address, nil)
	if err != nil {
		return nil, err
	}
	req.Header.Set("User-Agent", previewUserAgent)
	req.Header.Set("Accept", accept)
	return previewClient.Do(req)
}

// pageMeta collects the <meta> tags of a page's head, by property or name in
// lower case, and its <title> as "title" when there is no such meta tag.
func pageMeta(r io.Reader) map[string]string {
	meta := map[string]string{}
	z := html.NewTokenizer(r)
	inTitle := false
	var title strings.Builder
	for {
		switch z.Next() {
		case html.ErrorToken:
			return withTitle(meta, title.String())
		case html.StartTagToken, html.SelfClosingTagToken:
			tok := z.Token()
			switch tok.Data {
			case "meta":
				var key, content string
				for _, a := range tok.Attr {
					switch strings.ToLower(a.Key) {
					case "property", "name":
						if key == "" {
							key = strings.ToLower(strings.TrimSpace(a.Val))
						}
					case "content":
						content = strings.TrimSpace(a.Val)
					}
				}
				if _, seen := meta[key]; key != "" && content != "" && !seen {
					meta[key] = content
				}
			case "title":
				inTitle = true
			case "body":
				return withTitle(meta, title.String())
			}
		case html.TextToken:
			if inTitle {
				title.Write(z.Text())
			}
		case html.EndTagToken:
			switch tok, _ := z.TagName(); string(tok) {
			case "title":
				inTitle = false
			case "head":
				return withTitle(meta, title.String())
			}
		}
	}
}

func withTitle(meta map[string]string, title string) map[string]string {
	if _, ok := meta["title"]; !ok {
		meta["title"] = strings.Join(strings.Fields(title), " ")
	}
	return meta
}

// previewThumbnail fetches a page's picture and makes the small JPEG a link
// preview carries. It returns nil when there is no picture Go can read.
func previewThumbnail(ctx context.Context, address string) []byte {
	resp, err := previewGet(ctx, address, "image/*")
	if err != nil {
		return nil
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return nil
	}
	data, err := io.ReadAll(io.LimitReader(resp.Body, maxPreviewImage+1))
	if err != nil || len(data) > maxPreviewImage {
		return nil
	}
	decoded, _, err := image.Decode(bytes.NewReader(data))
	if err != nil {
		return nil
	}
	return thumbnail(decoded, previewThumbSize)
}

func firstOf(values ...string) string {
	for _, v := range values {
		if v = strings.TrimSpace(v); v != "" {
			return v
		}
	}
	return ""
}
