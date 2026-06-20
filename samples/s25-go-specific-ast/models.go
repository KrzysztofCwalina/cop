package shop

// A struct value type.
type Widget struct {
	Width int
}

// An interface — flagged by the demo check below.
type Drawable interface {
	Draw()
}

// Another struct.
type Point struct {
	X, Y int
}
