-- Simple 1:M parent-child schema
CREATE TABLE Parents (
    ParentId INTEGER PRIMARY KEY,
    Name TEXT NOT NULL
);

CREATE TABLE Children (
    ChildId INTEGER PRIMARY KEY,
    ParentId INTEGER NOT NULL,
    Name TEXT NOT NULL,
    FOREIGN KEY (ParentId) REFERENCES Parents(ParentId)
);

-- Sample data
INSERT INTO Parents (ParentId, Name) VALUES (1, 'Parent 1');
INSERT INTO Parents (ParentId, Name) VALUES (2, 'Parent 2');

INSERT INTO Children (ChildId, ParentId, Name) VALUES (1, 1, 'Child A');
INSERT INTO Children (ChildId, ParentId, Name) VALUES (2, 1, 'Child B');
